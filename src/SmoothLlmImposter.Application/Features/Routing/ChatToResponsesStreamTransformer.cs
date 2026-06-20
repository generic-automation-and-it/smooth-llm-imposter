using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Incremental, bounded-state Chat Completions → Responses translator for the
/// <c>/responses</c> downgrade path. It consumes SSE lines, emits Responses SSE frames,
/// and never buffers the upstream stream before yielding.
/// </summary>
internal sealed class ChatToResponsesStreamTransformer : IChatToResponsesTransformer
{
    public async IAsyncEnumerable<string> TransformStreamingAsync(
        IAsyncEnumerable<string> upstreamLines,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = new StreamState();
        var dataLines = new List<string>();

        await foreach (string line in upstreamLines.WithCancellation(cancellationToken))
        {
            if (line.Length == 0)
            {
                foreach (string frame in ProcessFrame(string.Join('\n', dataLines), state))
                {
                    yield return frame;
                }

                dataLines.Clear();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataLines.Add(line[5..].TrimStart());
            }
        }

        if (dataLines.Count > 0)
        {
            foreach (string frame in ProcessFrame(string.Join('\n', dataLines), state))
            {
                yield return frame;
            }
        }
    }

    public string TransformNonStreaming(string chatCompletionJson)
    {
        JsonObject root = ParseObject(chatCompletionJson);
        var state = new StreamState();
        StartResponse(root, state);

        if (root["choices"] is JsonArray choices)
        {
            foreach (JsonNode? choiceNode in choices)
            {
                if (choiceNode is not JsonObject choice)
                {
                    continue;
                }

                if (choice["message"] is JsonObject message)
                {
                    if (message["content"] is JsonValue content &&
                        content.GetValueKind() == JsonValueKind.String)
                    {
                        EnsureMessage(state);
                        state.Text.Append(content.GetValue<string>());
                    }

                    if (message["reasoning_content"] is JsonValue reasoning &&
                        reasoning.GetValueKind() == JsonValueKind.String)
                    {
                        state.Reasoning.Append(reasoning.GetValue<string>());
                    }

                    if (message["tool_calls"] is JsonArray toolCalls)
                    {
                        foreach (JsonNode? toolCallNode in toolCalls)
                        {
                            if (toolCallNode is JsonObject toolCall)
                            {
                                ApplyToolCallDelta(toolCall, state);
                            }
                        }
                    }
                }
            }
        }

        state.Usage = MapUsage(root["usage"]);
        CompleteOpenItems(state);
        return CreateResponseObject(state).ToJsonString();
    }

    private static IEnumerable<string> ProcessFrame(string data, StreamState state)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            yield break;
        }

        if (string.Equals(data.Trim(), "[DONE]", StringComparison.Ordinal))
        {
            if (state.Started && !state.Completed)
            {
                foreach (string frame in Complete(state))
                {
                    yield return frame;
                }
            }

            yield break;
        }

        JsonObject chunk = ParseObject(data);
        if (!state.Started)
        {
            StartResponse(chunk, state);
            yield return Frame("response.created", new JsonObject
            {
                ["type"] = "response.created",
                ["response"] = CreateResponseObject(state, "in_progress", includeOutput: false)
            });
            yield return Frame("response.in_progress", new JsonObject
            {
                ["type"] = "response.in_progress",
                ["response"] = CreateResponseObject(state, "in_progress", includeOutput: false)
            });
            yield return AddMessageItem(state);
            yield return AddContentPart(state);
        }

        if (chunk["usage"] is { } usage)
        {
            state.Usage = MapUsage(usage);
        }

        bool shouldComplete = false;
        if (chunk["choices"] is JsonArray choices)
        {
            foreach (JsonNode? choiceNode in choices)
            {
                if (choiceNode is not JsonObject choice)
                {
                    continue;
                }

                if (choice["delta"] is JsonObject delta)
                {
                    if (delta["content"] is JsonValue content &&
                        content.GetValueKind() == JsonValueKind.String)
                    {
                        string text = content.GetValue<string>();
                        if (text.Length > 0)
                        {
                            state.Text.Append(text);
                            yield return Frame("response.output_text.delta", new JsonObject
                            {
                                ["type"] = "response.output_text.delta",
                                ["item_id"] = state.MessageItem.Id,
                                ["output_index"] = state.MessageItem.OutputIndex,
                                ["content_index"] = 0,
                                ["delta"] = text
                            });
                        }
                    }

                    if (delta["reasoning_content"] is JsonValue reasoning &&
                        reasoning.GetValueKind() == JsonValueKind.String)
                    {
                        string text = reasoning.GetValue<string>();
                        if (text.Length > 0)
                        {
                            state.Reasoning.Append(text);
                            yield return Frame("response.reasoning_summary_text.delta", new JsonObject
                            {
                                ["type"] = "response.reasoning_summary_text.delta",
                                ["item_id"] = state.MessageItem.Id,
                                ["output_index"] = state.MessageItem.OutputIndex,
                                ["summary_index"] = 0,
                                ["delta"] = text
                            });
                        }
                    }

                    if (delta["tool_calls"] is JsonArray toolCalls)
                    {
                        foreach (JsonNode? toolCallNode in toolCalls)
                        {
                            if (toolCallNode is not JsonObject toolCall)
                            {
                                continue;
                            }

                            ToolCallState toolState = ApplyToolCallDelta(toolCall, state);
                            if (!toolState.Added)
                            {
                                toolState.Added = true;
                                yield return Frame("response.output_item.added", new JsonObject
                                {
                                    ["type"] = "response.output_item.added",
                                    ["output_index"] = toolState.OutputIndex,
                                    ["item"] = CreateToolItem(toolState, "in_progress")
                                });
                            }

                            string argumentsDelta = toolCall["function"]?["arguments"]?.GetValue<string>() ?? string.Empty;
                            if (argumentsDelta.Length > 0)
                            {
                                yield return Frame("response.function_call_arguments.delta", new JsonObject
                                {
                                    ["type"] = "response.function_call_arguments.delta",
                                    ["item_id"] = toolState.Id,
                                    ["output_index"] = toolState.OutputIndex,
                                    ["delta"] = argumentsDelta
                                });
                            }
                        }
                    }
                }

                if (choice["finish_reason"] is JsonValue finishReason &&
                    finishReason.GetValueKind() != JsonValueKind.Null)
                {
                    shouldComplete = true;
                }
            }
        }

        if (shouldComplete)
        {
            foreach (string frame in Complete(state))
            {
                yield return frame;
            }
        }
    }

    private static IEnumerable<string> Complete(StreamState state)
    {
        if (state.Completed)
        {
            yield break;
        }

        CompleteOpenItems(state);

        yield return Frame("response.output_text.done", new JsonObject
        {
            ["type"] = "response.output_text.done",
            ["item_id"] = state.MessageItem.Id,
            ["output_index"] = state.MessageItem.OutputIndex,
            ["content_index"] = 0,
            ["text"] = state.Text.ToString()
        });
        yield return Frame("response.content_part.done", new JsonObject
        {
            ["type"] = "response.content_part.done",
            ["item_id"] = state.MessageItem.Id,
            ["output_index"] = state.MessageItem.OutputIndex,
            ["content_index"] = 0,
            ["part"] = CreateOutputTextPart(state.Text.ToString())
        });
        yield return Frame("response.output_item.done", new JsonObject
        {
            ["type"] = "response.output_item.done",
            ["output_index"] = state.MessageItem.OutputIndex,
            ["item"] = CreateMessageItem(state, "completed")
        });

        foreach (ToolCallState tool in state.ToolCalls.Values.OrderBy(t => t.OutputIndex))
        {
            yield return Frame("response.function_call_arguments.done", new JsonObject
            {
                ["type"] = "response.function_call_arguments.done",
                ["item_id"] = tool.Id,
                ["output_index"] = tool.OutputIndex,
                ["arguments"] = tool.Arguments.ToString()
            });
            yield return Frame("response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["output_index"] = tool.OutputIndex,
                ["item"] = CreateToolItem(tool, "completed")
            });
        }

        yield return Frame("response.completed", new JsonObject
        {
            ["type"] = "response.completed",
            ["response"] = CreateResponseObject(state)
        });
    }

    private static void StartResponse(JsonObject source, StreamState state)
    {
        state.ResponseId = ToResponseId(source["id"]?.GetValue<string>());
        state.CreatedAt = source["created"]?.GetValue<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        state.Model = source["model"]?.GetValue<string>() ?? string.Empty;
        state.Started = true;
    }

    private static void EnsureMessage(StreamState state)
    {
        if (state.MessageItem.Id.Length > 0)
        {
            return;
        }

        state.MessageItem = new MessageState($"{state.ResponseId}_msg_0", OutputIndex: 0);
        state.NextOutputIndex = 1;
    }

    private static string AddMessageItem(StreamState state)
    {
        EnsureMessage(state);
        return Frame("response.output_item.added", new JsonObject
        {
            ["type"] = "response.output_item.added",
            ["output_index"] = state.MessageItem.OutputIndex,
            ["item"] = CreateMessageItem(state, "in_progress", includeContent: false)
        });
    }

    private static string AddContentPart(StreamState state) =>
        Frame("response.content_part.added", new JsonObject
        {
            ["type"] = "response.content_part.added",
            ["item_id"] = state.MessageItem.Id,
            ["output_index"] = state.MessageItem.OutputIndex,
            ["content_index"] = 0,
            ["part"] = CreateOutputTextPart(string.Empty)
        });

    private static ToolCallState ApplyToolCallDelta(JsonObject toolCall, StreamState state)
    {
        int index = toolCall["index"]?.GetValue<int>() ?? state.ToolCalls.Count;
        if (!state.ToolCalls.TryGetValue(index, out ToolCallState? toolState))
        {
            toolState = new ToolCallState(
                Index: index,
                OutputIndex: state.NextOutputIndex++,
                Id: ToItemId(toolCall["id"]?.GetValue<string>(), state.ResponseId, index),
                CallId: toolCall["id"]?.GetValue<string>() ?? $"call_{index}",
                Name: string.Empty);
            state.ToolCalls.Add(index, toolState);
        }

        if (toolCall["id"] is JsonValue id &&
            id.GetValueKind() == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(id.GetValue<string>()))
        {
            toolState.CallId = id.GetValue<string>();
        }

        if (toolCall["function"] is JsonObject function)
        {
            if (function["name"] is JsonValue name &&
                name.GetValueKind() == JsonValueKind.String &&
                !string.IsNullOrEmpty(name.GetValue<string>()))
            {
                toolState.Name = name.GetValue<string>();
            }

            if (function["arguments"] is JsonValue arguments &&
                arguments.GetValueKind() == JsonValueKind.String)
            {
                toolState.Arguments.Append(arguments.GetValue<string>());
            }
        }

        return toolState;
    }

    private static void CompleteOpenItems(StreamState state)
    {
        EnsureMessage(state);
        state.Completed = true;
    }

    private static JsonObject CreateResponseObject(
        StreamState state,
        string status = "completed",
        bool includeOutput = true)
    {
        var response = new JsonObject
        {
            ["id"] = state.ResponseId,
            ["object"] = "response",
            ["created_at"] = state.CreatedAt,
            ["status"] = status,
            ["model"] = state.Model,
        };

        if (includeOutput)
        {
            var output = new JsonArray { CreateMessageItem(state, "completed") };
            foreach (ToolCallState tool in state.ToolCalls.Values.OrderBy(t => t.OutputIndex))
            {
                output.Add(CreateToolItem(tool, "completed"));
            }

            response["output"] = output;
        }

        if (state.Usage is not null)
        {
            response["usage"] = state.Usage.DeepClone();
        }

        return response;
    }

    private static JsonObject CreateMessageItem(
        StreamState state,
        string status,
        bool includeContent = true)
    {
        var item = new JsonObject
        {
            ["id"] = state.MessageItem.Id,
            ["type"] = "message",
            ["status"] = status,
            ["role"] = "assistant"
        };

        if (includeContent)
        {
            item["content"] = new JsonArray { CreateOutputTextPart(state.Text.ToString()) };
        }

        return item;
    }

    private static JsonObject CreateOutputTextPart(string text) =>
        new()
        {
            ["type"] = "output_text",
            ["text"] = text,
            ["annotations"] = new JsonArray()
        };

    private static JsonObject CreateToolItem(ToolCallState tool, string status) =>
        new()
        {
            ["id"] = tool.Id,
            ["type"] = "function_call",
            ["status"] = status,
            ["call_id"] = tool.CallId,
            ["name"] = tool.Name,
            ["arguments"] = tool.Arguments.ToString()
        };

    private static JsonObject? MapUsage(JsonNode? usage)
    {
        if (usage is not JsonObject usageObject)
        {
            return null;
        }

        var mapped = new JsonObject();
        CopyNumber(usageObject, mapped, "prompt_tokens", "input_tokens");
        CopyNumber(usageObject, mapped, "completion_tokens", "output_tokens");
        CopyNumber(usageObject, mapped, "total_tokens", "total_tokens");

        foreach (KeyValuePair<string, JsonNode?> property in usageObject)
        {
            mapped[property.Key] ??= property.Value?.DeepClone();
        }

        return mapped;
    }

    private static void CopyNumber(JsonObject source, JsonObject target, string sourceName, string targetName)
    {
        if (source[sourceName] is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
        {
            target[targetName] = value.DeepClone();
        }
    }

    private static string Frame(string eventName, JsonObject data) =>
        $"event: {eventName}\ndata: {data.ToJsonString()}\n\n";

    private static JsonObject ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject
                ?? throw new RoutingException("Upstream Chat response must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new RoutingException($"Upstream Chat response is not valid JSON: {ex.Message}");
        }
    }

    private static string ToResponseId(string? sourceId) =>
        string.IsNullOrWhiteSpace(sourceId) ? $"resp_{Guid.NewGuid():N}" : sourceId.StartsWith("resp_", StringComparison.Ordinal) ? sourceId : $"resp_{sourceId}";

    private static string ToItemId(string? sourceId, string responseId, int index) =>
        string.IsNullOrWhiteSpace(sourceId) ? $"{responseId}_fc_{index}" : sourceId;

    private sealed class StreamState
    {
        public bool Started { get; set; }
        public bool Completed { get; set; }
        public string ResponseId { get; set; } = string.Empty;
        public long CreatedAt { get; set; }
        public string Model { get; set; } = string.Empty;
        public MessageState MessageItem { get; set; } = new(string.Empty, 0);
        public int NextOutputIndex { get; set; }
        public System.Text.StringBuilder Text { get; } = new();
        public System.Text.StringBuilder Reasoning { get; } = new();
        public JsonObject? Usage { get; set; }
        public SortedDictionary<int, ToolCallState> ToolCalls { get; } = new();
    }

    private sealed record MessageState(string Id, int OutputIndex);

    private sealed record ToolCallState(int Index, int OutputIndex, string Id, string CallId, string Name)
    {
        public bool Added { get; set; }
        public string CallId { get; set; } = CallId;
        public string Name { get; set; } = Name;
        public System.Text.StringBuilder Arguments { get; } = new();
    }
}
