using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;

namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

internal sealed class DecisionToolsFilter(
    IOptions<ImposterOptions> options,
    IDecisionModelClient client,
    ILogger<DecisionToolsFilter> logger) : IDecisionToolsFilter
{
    private const int ChunkSize = 20;
    private const int InstructionLimit = 300;
    private readonly DecisionAnswerCache _cache = new();

    public async Task<string> FilterAsync(string path, string body, CancellationToken cancellationToken)
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
            if (root["tools"] is not JsonArray { Count: > 0 } tools ||
                (shape == DecisionBodyShape.Responses &&
                 ((root.ContainsKey("previous_response_id") && root["previous_response_id"] is not null) ||
                  (root.ContainsKey("conversation") && root["conversation"] is not null))))
            {
                return body;
            }

            if (DecisionTool.UsesDeferredLoading(tools))
            {
                logger.LogDebug("Decisions filter {Shape}: deferred tool loading present, forwarding unfiltered", shape);
                return body;
            }

            string state = DecisionPrompt.Extract(root, shape);
            if (string.IsNullOrWhiteSpace(state))
            {
                return body;
            }

            DecisionTool[] candidates = DecisionTool.ReadCandidates(tools, root, shape);
            var removed = new HashSet<int>();
            foreach (DecisionTool[] chunk in candidates.Chunk(ChunkSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyDictionary<string, double> probabilities;
                try
                {
                    probabilities = await DecideAsync(BuildRequest(state, chunk), chunk, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception failure)
                {
                    LogFailOpen(failure);
                    break;
                }

                ApplyThreshold(probabilities, chunk, removed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (removed.Count == 0)
            {
                return body;
            }

            if (removed.Count == tools.Count)
            {
                logger.LogInformation("Decisions filter {Shape}: every tool scored below the threshold, kept all {Total}",
                    shape, tools.Count);
                return body;
            }

            string[] removedLabels = candidates.Where(tool => removed.Contains(tool.Index))
                .Select(tool => LogSafe(tool.Label)).ToArray();
            DecisionTool.PruneChoice(root, tools, removed, shape);
            foreach (int index in removed.OrderDescending())
            {
                tools.RemoveAt(index);
            }

            logger.LogInformation("Decisions filter {Shape}: kept {Kept}/{Total} tools in {ElapsedMs} ms, removed {Removed}",
                shape, tools.Count, tools.Count + removed.Count, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                string.Join(",", removedLabels));
            return DecisionJson.ReplaceTopLevel(body, root, "tools", "tool_choice");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            LogFailOpen(failure);
            return body;
        }
    }

    private JsonObject BuildRequest(string state, DecisionTool[] chunk)
    {
        var questions = new JsonObject();
        foreach (DecisionTool tool in chunk)
        {
            string instructions = $"Will the assistant need the tool '{tool.Label}' to carry out the user's request?";
            if (!string.IsNullOrWhiteSpace(tool.Description))
            {
                instructions += $" Tool: {tool.Description.Trim()}";
            }

            questions[tool.Key] = new JsonObject
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

        return new JsonObject
        {
            ["model"] = options.Value.DecisionsTools.Model,
            ["state"] = state,
            ["questions"] = questions
        };
    }

    private async Task<IReadOnlyDictionary<string, double>> DecideAsync(
        JsonObject request, DecisionTool[] chunk, CancellationToken cancellationToken)
    {
        string key = DecisionAnswerCache.KeyFor(request);
        if (_cache.TryGet(key, out IReadOnlyDictionary<string, double> cached))
        {
            logger.LogDebug("Decisions chunk of {Count} answered from cache", chunk.Length);
            return cached;
        }

        JsonObject envelope = await client.DecideAsync(request, cancellationToken);
        if (envelope["answers"] is not JsonObject answers)
        {
            throw new InvalidOperationException("Invalid decision envelope.");
        }

        Dictionary<string, double> probabilities = ReadProbabilities(answers, chunk);
        if (probabilities.Count == chunk.Length)
        {
            _cache.Add(key, probabilities);
        }

        return probabilities;
    }

    private Dictionary<string, double> ReadProbabilities(JsonObject answers, DecisionTool[] chunk)
    {
        var probabilities = new Dictionary<string, double>();
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
        }

        return probabilities;
    }

    private void ApplyThreshold(IReadOnlyDictionary<string, double> probabilities, DecisionTool[] chunk, HashSet<int> removed)
    {
        foreach (DecisionTool tool in chunk)
        {
            if (probabilities.TryGetValue(tool.Key, out double probability) &&
                probability < options.Value.DecisionsTools.MinProbability)
            {
                removed.Add(tool.Index);
            }
        }
    }

    private static string LogSafe(string label) =>
        new(label.Select(character => char.IsControl(character) ? '?' : character).ToArray());

    private void LogFailOpen(Exception failure) =>
        logger.LogWarning("Decisions filter failed open: {Reason}", failure.GetType().Name);
}
