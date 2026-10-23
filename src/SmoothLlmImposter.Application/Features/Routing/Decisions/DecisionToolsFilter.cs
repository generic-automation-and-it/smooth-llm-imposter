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

            DecisionsToolsOptions settings = options.Value.DecisionsTools;
            if (tools.Count <= settings.MinKeepTools)
            {
                logger.LogDebug("Decisions filter {Shape}: {Total} tools do not exceed the minimum-kept floor, forwarding unfiltered",
                    shape, tools.Count);
                return body;
            }

            string state = DecisionPrompt.Extract(root, shape);
            if (string.IsNullOrWhiteSpace(state))
            {
                return body;
            }

            DecisionTool[] candidates = DecisionTool.ReadCandidates(tools, root, shape, AlwaysKeepNames(settings));
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
                    LogFailOpen(failure);
                    break;
                }
            }

            var removed = candidates
                .Where(tool => probabilities.TryGetValue(tool.Key, out double probability) &&
                    probability < options.Value.DecisionsTools.MinProbability)
                .Select(tool => tool.Index)
                .ToHashSet();

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

            int restored = RestoreMinimum(removed, candidates, probabilities, tools.Count, settings.MinKeepTools);
            if (restored > 0)
            {
                logger.LogDebug("Decisions filter {Shape}: minimum-kept floor restored {Restored} highest-scoring tools",
                    shape, restored);
            }

            if (removed.Count == 0)
            {
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

    private static HashSet<string> AlwaysKeepNames(DecisionsToolsOptions settings) =>
        settings.AlwaysKeep.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

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

        JsonObject envelope = await client.DecideAsync(new JsonObject
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

    private void LogFailOpen(Exception failure) =>
        logger.LogWarning("Decisions filter failed open: {Reason} (status {StatusCode}, model {Model})",
            failure.GetType().Name, (int?)(failure as HttpRequestException)?.StatusCode, LogSafe(options.Value.DecisionsTools.Model));
}
