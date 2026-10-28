using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Application.Features.Routing.Optimization;

namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

internal sealed class DecisionToolsFilter(
    IOptions<ImposterOptions> options,
    IDecisionModelClient client,
    DecisionCircuitBreaker circuitBreaker,
    TimeProvider timeProvider,
    ILogger<DecisionToolsFilter> logger) : IDecisionToolsFilter, IOptimizationStrategy
{
    private const int ChunkSize = 20;
    private const int InstructionLimit = 300;
    private const int SessionTokenLength = 12;
    internal const int MaxStickyTools = 1024;
    private static readonly string[] CapabilityGateways = ["ToolSearch", "Agent"];
    private readonly DecisionAnswerCache _cache = new();
    private readonly SessionScopedMemory<HashSet<string>> _sticky =
        new(timeProvider, () => new HashSet<string>(StringComparer.Ordinal));

    public string Name => "tools";

    public bool Enabled => options.Value.DecisionsTools.Enabled;

    public async Task<OptimizationEdit> OptimizeAsync(
        OptimizationRequest request, CancellationToken cancellationToken)
    {
        string filtered = await FilterAsync(
            request.Path, request.Body, request.CallerHeaders, request.InboundBody, cancellationToken);
        if (ReferenceEquals(filtered, request.Body))
        {
            return OptimizationEdit.None;
        }

        JsonObject original = JsonNodeMaterializer.ParseObject(request.Body);
        JsonObject after = JsonNodeMaterializer.ParseObject(filtered);
        var patches = new List<OptimizationPatch>();
        AddIfChanged(original, after, "tools", patches);
        AddIfChanged(original, after, "tool_choice", patches);
        return patches.Count == 0 ? OptimizationEdit.None : new OptimizationEdit(patches);
    }

    private static void AddIfChanged(
        JsonObject original, JsonObject after, string name, List<OptimizationPatch> patches)
    {
        if (!JsonNode.DeepEquals(original[name], after[name]))
        {
            patches.Add(new OptimizationPatch($"/{name}", after[name]?.DeepClone()));
        }
    }

    public async Task<string> FilterAsync(
        string path, string body, CallerHeaders callerHeaders, string inboundBody, CancellationToken cancellationToken)
    {
        if (!options.Value.DecisionsTools.Enabled)
        {
            logger.LogDebug("Decisions filter disabled — forwarding without intercept");
            return body;
        }

        DecisionBodyShape shape = DecisionJson.ShapeFor(path);
        if (shape == DecisionBodyShape.Unknown || string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        long started = Stopwatch.GetTimestamp();
        try
        {
            JsonObject root = JsonNodeMaterializer.ParseObject(body);
            if (root["tools"] is not JsonArray { Count: > 0 } tools)
            {
                LogSkipped(shape, "no top-level tools array");
                return body;
            }

            if (shape == DecisionBodyShape.Responses &&
                ((root.ContainsKey("previous_response_id") && root["previous_response_id"] is not null) ||
                 (root.ContainsKey("conversation") && root["conversation"] is not null)))
            {
                LogSkipped(shape, "server-held conversation state");
                return body;
            }

            if (DecisionTool.UsesDeferredLoading(tools))
            {
                LogSkipped(shape, "deferred tool loading present");
                return body;
            }

            DecisionsToolsOptions settings = options.Value.DecisionsTools;
            if (tools.Count <= settings.MinKeepTools)
            {
                LogSkipped(shape, $"{tools.Count} tools do not exceed the minimum-kept floor");
                return body;
            }

            string state = DecisionPrompt.Extract(root, shape);
            if (string.IsNullOrWhiteSpace(state))
            {
                LogSkipped(shape, "no user request in the conversation state");
                return body;
            }

            SessionIdentity identity = SessionIdentityResolver.Resolve(callerHeaders, inboundBody);
            string session = SessionToken(identity);
            string? stickyScope = StickyScope(settings, shape, root, callerHeaders, identity);
            DecisionTool[] candidates = WithoutSticky(
                DecisionTool.ReadCandidates(tools, root, shape, AlwaysKeepNames(settings)), stickyScope, shape);
            var probabilities = new Dictionary<string, double>();
            var pending = new List<DecisionTool>();
            foreach (DecisionTool tool in candidates)
            {
                if (_cache.TryGet(CacheKey(state, tool), out double cached))
                {
                    probabilities[tool.Key] = cached;
                }
                else
                {
                    pending.Add(tool);
                }
            }

            if (probabilities.Count > 0)
            {
                logger.LogDebug("Decisions filter {Shape}: {Cached}/{Total} tools answered from cache",
                    shape, probabilities.Count, candidates.Length);
            }

            bool failedOpen = false;
            if (pending.Count > 0)
            {
                DecisionCircuitBreaker.Admission admission = circuitBreaker.TryEnter();
                if (admission == DecisionCircuitBreaker.Admission.Skip)
                {
                    LogSkipped(shape, "circuit-open");
                    return body;
                }

                bool succeeded = false;
                try
                {
                    if (admission == DecisionCircuitBreaker.Admission.Trial)
                    {
                        logger.LogInformation("Decisions filter {Shape} via model {Model} at {Endpoint}, session {Session}: circuit trial call",
                            shape, LogSafe(settings.Model), Endpoint(settings), session);
                    }

                    foreach (DecisionTool[] chunk in pending.Chunk(ChunkSize))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            await DecideAsync(state, chunk, probabilities, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception failure)
                        {
                            circuitBreaker.RecordFailure(failure, admission == DecisionCircuitBreaker.Admission.Trial, LogSafe(settings.Model), Endpoint(settings));
                            LogFailOpen(shape, session, failure);
                            failedOpen = true;
                            break;
                        }
                    }

                    if (!failedOpen)
                    {
                        circuitBreaker.RecordSuccess(
                            admission == DecisionCircuitBreaker.Admission.Trial, LogSafe(settings.Model), Endpoint(settings));
                        succeeded = true;
                    }
                }
                finally
                {
                    if (admission == DecisionCircuitBreaker.Admission.Trial && !succeeded)
                    {
                        circuitBreaker.AbandonTrial();
                    }
                }
            }

            var removed = candidates
                .Where(tool => probabilities.TryGetValue(tool.Key, out double probability) &&
                    probability < options.Value.DecisionsTools.MinProbability)
                .Select(tool => tool.Index)
                .ToHashSet();

            cancellationToken.ThrowIfCancellationRequested();
            if (removed.Count > 0 && removed.Count == tools.Count)
            {
                logger.LogInformation("Decisions filter {Shape} via model {Model} at {Endpoint}, session {Session}: every tool scored below the threshold, kept all {Total}",
                    shape, LogSafe(settings.Model), Endpoint(settings), session, tools.Count);
                return body;
            }

            int restored = removed.Count == 0
                ? 0
                : RestoreMinimum(removed, candidates, probabilities, tools.Count, settings.MinKeepTools);
            if (restored > 0)
            {
                logger.LogDebug("Decisions filter {Shape}: minimum-kept floor restored {Restored} highest-scoring tools",
                    shape, restored);
            }

            if (stickyScope is not null && candidates.All(tool => probabilities.ContainsKey(tool.Key)))
            {
                RecordSticky(stickyScope, DecisionTool.KnownLabels(tools, shape, removed));
            }

            if (removed.Count == 0)
            {
                if (!failedOpen)
                {
                    logger.LogInformation("Decisions filter {Shape} via model {Model} at {Endpoint}, session {Session}: forwarded unfiltered, no tool scored below the threshold",
                        shape, LogSafe(settings.Model), Endpoint(settings), session);
                }

                return body;
            }

            string[] removedLabels = candidates.Where(tool => removed.Contains(tool.Index))
                .Select(tool => LogSafe(tool.Label)).ToArray();
            DecisionTool.PruneChoice(root, tools, removed, shape);
            foreach (int index in removed.OrderDescending())
            {
                tools.RemoveAt(index);
            }

            logger.LogInformation("Decisions filter {Shape} via model {Model} at {Endpoint}, session {Session}: kept {Kept}/{Total} tools in {ElapsedMs} ms, removed {Removed}",
                shape, LogSafe(settings.Model), Endpoint(settings), session, tools.Count, tools.Count + removed.Count,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, string.Join(",", removedLabels));
            return DecisionJson.ReplaceTopLevel(body, root, "tools", "tool_choice");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            DecisionsToolsOptions outerSettings = options.Value.DecisionsTools;
            logger.LogWarning("Decisions filter {Shape} via model {Model} at {Endpoint} failed open: {Reason}",
                shape, LogSafe(outerSettings.Model), Endpoint(outerSettings), failure.GetType().Name);
            return body;
        }
    }

    private static string? StickyScope(
        DecisionsToolsOptions settings, DecisionBodyShape shape, JsonObject root, CallerHeaders callerHeaders, SessionIdentity identity)
    {
        if (!settings.StickyPerSession || !IsCaptured(identity))
        {
            return null;
        }

        return SessionScope.Key(shape.ToString(), DecisionJson.Text(root["model"]), identity.Value!, callerHeaders);
    }

    private static bool IsCaptured(SessionIdentity identity) =>
        identity.Source == SessionIdentitySource.Captured && identity.HasValue;

    private static string SessionToken(SessionIdentity identity) =>
        IsCaptured(identity) ? SessionScope.Hash(identity.Value!)[..SessionTokenLength] : "none";

    private DecisionTool[] WithoutSticky(DecisionTool[] candidates, string? stickyScope, DecisionBodyShape shape)
    {
        if (stickyScope is null)
        {
            return candidates;
        }

        HashSet<string> sticky = _sticky.Use(stickyScope, labels => labels.ToHashSet(StringComparer.Ordinal));
        DecisionTool[] remaining = candidates.Where(tool => !sticky.Contains(SessionScope.Hash(tool.Label))).ToArray();
        if (remaining.Length < candidates.Length)
        {
            logger.LogDebug("Decisions filter {Shape}: {Sticky} sticky tools kept without asking",
                shape, candidates.Length - remaining.Length);
        }
        return remaining;
    }

    private void RecordSticky(string stickyScope, string[] labels)
    {
        string[] keys = labels.Select(SessionScope.Hash).ToArray();
        _sticky.Use(stickyScope, sticky =>
        {
            if (sticky.Count + keys.Count(key => !sticky.Contains(key)) <= MaxStickyTools)
            {
                sticky.UnionWith(keys);
            }
        });
    }

    private static HashSet<string> AlwaysKeepNames(DecisionsToolsOptions settings) =>
        settings.AlwaysKeep.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(CapabilityGateways).ToHashSet();

    private static int RestoreMinimum(
        HashSet<int> removed,
        DecisionTool[] candidates,
        Dictionary<string, double> probabilities,
        int total,
        int minimumKept)
    {
        int needed = minimumKept - (total - removed.Count);
        if (needed <= 0)
        {
            return 0;
        }

        int[] restored = candidates
            .Where(tool => removed.Contains(tool.Index))
            .OrderByDescending(tool => probabilities[tool.Key])
            .ThenBy(tool => tool.Index)
            .Take(needed)
            .Select(tool => tool.Index)
            .ToArray();
        removed.ExceptWith(restored);
        return restored.Length;
    }

    private static JsonObject Question(DecisionTool tool)
    {
        string instructions = $"Will the assistant need the tool '{tool.Label}' to carry out the user's request?";
        if (!string.IsNullOrWhiteSpace(tool.Description))
        {
            instructions += $" Tool: {tool.Description.Trim()}";
        }

        return new JsonObject
        {
            ["type"] = "choice",
            ["instructions"] = instructions[..Math.Min(instructions.Length, InstructionLimit)],
            ["criteria"] = new JsonObject
            {
                ["use"] = "the request needs what this tool does, or names this tool",
                ["skip"] = "the request can be done fully without this tool"
            }
        };
    }

    private string CacheKey(string state, DecisionTool tool) =>
        DecisionAnswerCache.KeyFor(options.Value.DecisionsTools.Model, state, Question(tool));

    private async Task DecideAsync(
        string state, DecisionTool[] chunk, Dictionary<string, double> probabilities, CancellationToken cancellationToken)
    {
        var questions = new JsonObject();
        foreach (DecisionTool tool in chunk)
        {
            questions[tool.Key] = Question(tool);
        }

        JsonObject envelope = await client.DecideAsync(DecisionToolsConsumer.SectionName, new JsonObject
        {
            ["model"] = options.Value.DecisionsTools.Model,
            ["state"] = state,
            ["questions"] = questions
        }, cancellationToken);
        if (envelope["answers"] is not JsonObject answers)
        {
            throw new InvalidOperationException("Invalid decision envelope.");
        }

        foreach (DecisionTool tool in chunk)
        {
            if (answers[tool.Key] is not JsonObject answer || answer["probabilities"] is not JsonObject scores ||
                scores["use"] is not JsonValue value || !value.TryGetValue(out double probability) ||
                !double.IsFinite(probability) || probability is < 0 or > 1)
            {
                continue;
            }

            logger.LogDebug("Decisions tool {Name}: P(use)={Probability}", LogSafe(tool.Label), probability);
            probabilities[tool.Key] = probability;
            _cache.Add(CacheKey(state, tool), probability);
        }
    }

    private static string LogSafe(string label) =>
        new(label.Select(character => char.IsControl(character) ? '?' : character).ToArray());

    private void LogSkipped(DecisionBodyShape shape, string reason) =>
        logger.LogInformation("Decisions filter {Shape}: forwarded unfiltered, {Reason}", shape, reason);

    private void LogFailOpen(DecisionBodyShape shape, string session, Exception failure)
    {
        DecisionsToolsOptions settings = options.Value.DecisionsTools;
        logger.LogWarning("Decisions filter {Shape} via model {Model} at {Endpoint}, session {Session} failed open: {Reason} (status {StatusCode})",
            shape, LogSafe(settings.Model), Endpoint(settings), session, failure.GetType().Name,
            (int?)(failure as HttpRequestException)?.StatusCode);
    }

    private static string Endpoint(DecisionsToolsOptions settings) =>
        Uri.TryCreate(settings.BaseUrl.TrimEnd('/') + settings.Path, UriKind.Absolute, out Uri? endpoint)
            ? LogSafe(endpoint.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped))
            : "unparsed";
}
