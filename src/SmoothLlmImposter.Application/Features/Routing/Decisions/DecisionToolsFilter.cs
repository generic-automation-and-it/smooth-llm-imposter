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
                JsonObject envelope;
                try
                {
                    envelope = await client.DecideAsync(BuildRequest(state, chunk), cancellationToken);
                    if (envelope["answers"] is not JsonObject)
                    {
                        throw new InvalidOperationException("Invalid decision envelope.");
                    }
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

                ApplyAnswers(envelope["answers"]!.AsObject(), chunk, removed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (removed.Count == 0)
            {
                return body;
            }

            DecisionTool.PruneChoice(root, tools, removed, shape);
            foreach (int index in removed.OrderDescending())
            {
                tools.RemoveAt(index);
            }

            if (tools.Count == 0)
            {
                root.Remove("tools");
                root.Remove("tool_choice");
            }

            logger.LogInformation("Decisions filter {Path}: kept {Kept}/{Total} tools in {ElapsedMs} ms",
                path, tools.Count, tools.Count + removed.Count, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return root.ToJsonString();
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
            string instructions = $"{tool.Label} {tool.Description}".Trim();
            questions[tool.Key] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] = instructions[..Math.Min(instructions.Length, 120)],
                ["criteria"] = new JsonObject
                {
                    ["use"] = "the assistant will call this tool to answer",
                    ["skip"] = "not needed"
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

    private void ApplyAnswers(JsonObject answers, DecisionTool[] chunk, HashSet<int> removed)
    {
        foreach (DecisionTool tool in chunk)
        {
            if (answers[tool.Key] is not JsonObject answer || answer["probabilities"] is not JsonObject probabilities ||
                probabilities["use"] is not JsonValue value || !value.TryGetValue(out double probability) ||
                !double.IsFinite(probability) || probability is < 0 or > 1)
            {
                continue;
            }

            logger.LogDebug("Decisions tool {Name}: P(use)={Probability}", tool.Label, probability);
            if (probability < options.Value.DecisionsTools.MinProbability)
            {
                removed.Add(tool.Index);
            }
        }
    }

    private void LogFailOpen(Exception failure) =>
        logger.LogWarning("Decisions filter failed open: {Reason}", failure.GetType().Name);
}
