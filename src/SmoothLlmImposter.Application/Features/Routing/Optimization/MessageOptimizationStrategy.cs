using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Optimizer;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.Features.Routing.Decisions;

namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

internal sealed class MessageOptimizationStrategy(
    IOptions<ImposterOptions> options,
    IMessageGate gate,
    IOptimizerClient optimizer,
    IMessageChecksumCache cache,
    MessageGateCircuitBreaker gateCircuit,
    MessageOptimizerCircuitBreaker optimizerCircuit,
    ILogger<MessageOptimizationStrategy> logger) : IOptimizationStrategy
{
    internal const string InstructionRevision = "1";

    internal const string Instructions =
        "Rewrite one user message so it is shorter and cleaner. Remove filler, pleasantries, hedging and redundant phrasing. " +
        "Preserve code, inline code, URLs, paths, commands, technical terms, proper nouns, numbers, versions, environment variables, " +
        "negations and markdown structure exactly. Keep tools, skills, hooks, plugins, rules and external links exactly. " +
        "Do not summarize code blocks, shorten paths, or drop a negation you think is redundant. " +
        "Return only the rewritten message, with no commentary or instructions.";

    public string Name => "messages";

    public bool Enabled => options.Value.LlmMessages.Enabled;

    public async Task<OptimizationEdit> OptimizeAsync(OptimizationRequest request, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return OptimizationEdit.None;
        }

        DecisionBodyShape shape = DecisionJson.ShapeFor(request.Path);
        if (shape is not (DecisionBodyShape.Chat or DecisionBodyShape.Anthropic))
        {
            return OptimizationEdit.None;
        }

        JsonObject root;
        try
        {
            root = JsonNodeMaterializer.ParseObject(request.Body);
        }
        catch (RoutingException)
        {
            return OptimizationEdit.None;
        }

        if (root["messages"] is not JsonArray messages)
        {
            return OptimizationEdit.None;
        }

        SessionIdentity session = SessionIdentityResolver.Resolve(request.CallerHeaders, request.InboundBody);
        string version = $"{options.Value.LlmMessages.Model}:{InstructionRevision}";
        IReadOnlyList<string> protectedNames = ToolNames(root);
        int minChars = options.Value.LlmMessages.MinChars;
        var patches = new List<OptimizationPatch>();

        for (int index = 0; index < messages.Count; index++)
        {
            if (messages[index] is not JsonObject message ||
                DecisionJson.Text(message["role"]) != "user" ||
                CarriesToolResult(message))
            {
                continue;
            }

            List<(string Pointer, string Text)> blocks = TextBlocks(message, index);
            if (blocks.Count == 0 ||
                WhoMessageResponder.Switches.Any(switchText =>
                    string.Equals(string.Join("\n", blocks.Select(block => block.Text)).Trim(), switchText, StringComparison.Ordinal)))
            {
                continue;
            }

            foreach ((string pointer, string text) in blocks)
            {
                string? rewritten = await RewriteBlockAsync(
                    text, session, request.CallerHeaders, version, protectedNames, minChars, cancellationToken);
                if (rewritten is not null)
                {
                    patches.Add(new OptimizationPatch(pointer, JsonValue.Create(rewritten)));
                }
            }
        }

        return patches.Count == 0
            ? OptimizationEdit.None
            : new OptimizationEdit(patches);
    }

    private async Task<string?> RewriteBlockAsync(
        string text,
        SessionIdentity session,
        CallerHeaders headers,
        string version,
        IReadOnlyList<string> protectedNames,
        int minChars,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<HumanSpanExtractor.Segment> segments = HumanSpanExtractor.Split(text);
        var humanRewrites = new List<string>();
        bool changed = false;

        foreach (HumanSpanExtractor.Segment segment in segments)
        {
            if (!segment.IsHuman)
            {
                continue;
            }

            string original = segment.Text;
            if (string.IsNullOrWhiteSpace(original))
            {
                humanRewrites.Add(original);
                continue;
            }

            // MinChars is per human segment, not per whole message (see LlmMessagesOptions.MinChars).
            if (minChars > 0 && original.Trim().Length < minChars)
            {
                humanRewrites.Add(original);
                continue;
            }

            string forwarded = await ForwardAsync(
                original, session, headers, version, protectedNames, cancellationToken);
            changed |= !string.Equals(forwarded, original, StringComparison.Ordinal);
            humanRewrites.Add(forwarded);
        }

        return changed
            ? HumanSpanExtractor.Reassemble(segments, humanRewrites)
            : null;
    }

    private async Task<string> ForwardAsync(
        string original,
        SessionIdentity session,
        CallerHeaders headers,
        string version,
        IReadOnlyList<string> protectedNames,
        CancellationToken cancellationToken)
    {
        if (cache.TryGet(session, headers, original, version, out string cached))
        {
            logger.LogDebug("Message optimization: cache hit ({Length} chars)", cached.Length);
            return cached;
        }

        if (options.Value.DecisionsMessages.Enabled)
        {
            SectionCircuitBreaker.Admission admission = gateCircuit.TryEnter();
            if (admission == SectionCircuitBreaker.Admission.Skip)
            {
                logger.LogInformation("Message gate circuit open: skipping the gate and continuing to the optimizer");
            }
            else
            {
                if (admission == SectionCircuitBreaker.Admission.Trial)
                {
                    logger.LogInformation(
                        "Message gate circuit trial via model {Model} at {Endpoint}",
                        gate.Model, gate.Endpoint);
                }

                try
                {
                    if (!await gate.CanOptimizeAsync(original, cancellationToken))
                    {
                        gateCircuit.RecordSuccess(admission == SectionCircuitBreaker.Admission.Trial, gate.Model, gate.Endpoint);
                        cache.Store(session, headers, original, version, original);
                        logger.LogDebug("Message optimization: declined by gate ({Length} chars)", original.Length);
                        return original;
                    }

                    gateCircuit.RecordSuccess(admission == SectionCircuitBreaker.Admission.Trial, gate.Model, gate.Endpoint);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (admission == SectionCircuitBreaker.Admission.Trial)
                    {
                        gateCircuit.AbandonTrial();
                    }
                    throw;
                }
                catch (Exception failure)
                {
                    gateCircuit.RecordFailure(
                        failure, admission == SectionCircuitBreaker.Admission.Trial, gate.Model, gate.Endpoint);
                    if (admission == SectionCircuitBreaker.Admission.Trial)
                    {
                        gateCircuit.AbandonTrial();
                    }
                    // A failed gate continues to the optimizer (LADR-07); it does not fail the request.
                    logger.LogWarning(failure, "Message gate failed; continuing to optimizer.");
                }
            }
        }

        string? rewrite;
        try
        {
            rewrite = await TryOptimizeAsync(Instructions, original, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            cache.Store(session, headers, original, version, original);
            logger.LogWarning(failure, "Message optimizer failed; forwarding the original message.");
            return original;
        }

        if (rewrite is null)
        {
            cache.Store(session, headers, original, version, original);
            return original;
        }

        string forwardedText = await MessageRewriteValidator.ValidateWithRetryAsync(
            original,
            rewrite,
            async (text, failed, token) =>
                await TryOptimizeAsync(RetryInstructions(failed), text, token) ?? text,
            protectedNames,
            Instructions,
            cancellationToken);

        cache.Store(session, headers, original, version, forwardedText);
        logger.LogDebug(
            "Message optimization: {Outcome} ({OriginalLength}->{ForwardedLength} chars)",
            forwardedText == original ? "original" : "rewritten",
            original.Length,
            forwardedText.Length);
        return forwardedText;
    }

    private async Task<string?> TryOptimizeAsync(
        string instructions, string text, CancellationToken cancellationToken)
    {
        SectionCircuitBreaker.Admission admission = optimizerCircuit.TryEnter();
        if (admission == SectionCircuitBreaker.Admission.Skip)
        {
            logger.LogInformation("Message optimizer circuit open: skipping the call and forwarding the original");
            return null;
        }

        string model = options.Value.LlmMessages.Model;
        string endpoint = SectionCircuitBreaker.Endpoint(
            options.Value.LlmMessages.BaseUrl, options.Value.LlmMessages.Path);
        if (admission == SectionCircuitBreaker.Admission.Trial)
        {
            logger.LogInformation(
                "Message optimizer circuit trial via model {Model} at {Endpoint}", model, endpoint);
        }

        try
        {
            string rewrite = await optimizer.OptimizeAsync(instructions, text, cancellationToken);
            optimizerCircuit.RecordSuccess(admission == SectionCircuitBreaker.Admission.Trial, model, endpoint);
            return rewrite;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (admission == SectionCircuitBreaker.Admission.Trial)
            {
                optimizerCircuit.AbandonTrial();
            }
            throw;
        }
        catch (Exception failure)
        {
            optimizerCircuit.RecordFailure(
                failure, admission == SectionCircuitBreaker.Admission.Trial, model, endpoint);
            if (admission == SectionCircuitBreaker.Admission.Trial)
            {
                optimizerCircuit.AbandonTrial();
            }
            throw;
        }
    }

    private static string RetryInstructions(IReadOnlyList<string> failedElements) =>
        Instructions +
        "\nThe previous rewrite failed these checks: " + string.Join("; ", failedElements) +
        ". Return only the corrected message and preserve every listed element exactly.";

    private static List<(string Pointer, string Text)> TextBlocks(JsonObject message, int messageIndex)
    {
        var blocks = new List<(string Pointer, string Text)>();
        JsonNode? content = message["content"];

        if (content is JsonValue value && value.TryGetValue(out string? single))
        {
            blocks.Add(($"/messages/{messageIndex}/content", single));
            return blocks;
        }

        if (content is not JsonArray parts)
        {
            return blocks;
        }

        for (int partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            if (parts[partIndex] is JsonObject part &&
                DecisionJson.Text(part["type"]) == "text" &&
                part["text"] is JsonValue textValue &&
                textValue.TryGetValue(out string? text))
            {
                blocks.Add(($"/messages/{messageIndex}/content/{partIndex}/text", text));
            }
        }

        return blocks;
    }

    private static bool CarriesToolResult(JsonObject message)
    {
        if (DecisionJson.Text(message["type"]) == "tool_result")
        {
            return true;
        }

        return message["content"] is JsonArray parts &&
            parts.OfType<JsonObject>().Any(part => DecisionJson.Text(part["type"]) == "tool_result");
    }

    private static IReadOnlyList<string> ToolNames(JsonObject root)
    {
        if (root["tools"] is not JsonArray tools)
        {
            return [];
        }

        var names = new List<string>();
        foreach (JsonObject tool in tools.OfType<JsonObject>())
        {
            string name = tool["function"] is JsonObject function
                ? DecisionJson.Text(function["name"])
                : DecisionJson.Text(tool["name"]);
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }
        return names;
    }
}
