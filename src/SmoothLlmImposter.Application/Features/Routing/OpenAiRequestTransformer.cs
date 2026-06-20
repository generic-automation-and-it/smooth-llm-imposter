using System.Text.Json;
using System.Text.Json.Nodes;
using SmoothLlmImposter.Application.Features.Routing.Normalization;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// OpenAI-dialect transform: rewrites <c>model</c> and, when caching is enabled, sets
/// <c>prompt_cache_key</c> to the original (inbound) model so requests for the same imposter model
/// share an upstream cache bucket. OpenAI caches automatically, so no content restructuring is needed.
/// On a matched imposter route that opts in, an optional request-normalization stage (HLD 004) runs
/// first; passthrough/default routes are never normalized.
/// </summary>
internal sealed class OpenAiRequestTransformer : IRequestTransformer
{
    private readonly IReadOnlyDictionary<RequestNormalization, IRequestNormalizer> _normalizers;

    public OpenAiRequestTransformer(IEnumerable<IRequestNormalizer> normalizers) =>
        _normalizers = normalizers.ToDictionary(n => n.Kind);

    public ApiDialect Dialect => ApiDialect.OpenAi;

    public string Transform(string requestBody, RouteDecision decision, string inboundModel)
    {
        JsonObject root = ParseObject(requestBody);

        // Normalize before the Responses→Chat conversion: a flattened namespace yields flat function
        // tools that ConvertTools then nests for chat upstreams, while responses upstreams keep them flat.
        // Scoped to matched imposter routes (LADR-01) and the provider's opt-in (LADR-03); None never
        // resolves a normalizer, so an un-opted-in provider is byte-transparent.
        if (decision.IsImposter &&
            _normalizers.TryGetValue(decision.Provider.RequestNormalization, out IRequestNormalizer? normalizer))
        {
            normalizer.Normalize(root);
        }

        if (decision.Provider.OpenAiUpstreamApi == OpenAiUpstreamApi.ChatCompletions)
        {
            root = ToChatCompletions(root);
        }

        root["model"] = decision.TargetModel;

        if (decision.CachingEnabled && decision.Provider.OpenAiUpstreamApi == OpenAiUpstreamApi.Responses)
        {
            root["prompt_cache_key"] = inboundModel;
        }

        return root.ToJsonString();
    }

    private static JsonObject ToChatCompletions(JsonObject root)
    {
        RejectResponsesStatePointers(root);

        var chat = new JsonObject();

        AddIfPresent(root, chat, "stream");
        AddIfPresent(root, chat, "temperature");
        AddIfPresent(root, chat, "top_p");

        // Responses and Chat Completions use different tool/tool_choice schemas: Responses puts the function
        // fields flat on the tool object ({type,name,parameters,...}) while Chat nests them under "function".
        // Copy verbatim would make an OpenAI-compatible upstream (opencode) 400 on the flat shape, so convert.
        if (ConvertTools(root["tools"]) is { } tools)
        {
            chat["tools"] = tools;
        }

        if (ConvertToolChoice(root["tool_choice"]) is { } toolChoice)
        {
            chat["tool_choice"] = toolChoice;
        }

        AddIfPresent(root, chat, "parallel_tool_calls");
        if (ConvertResponseFormat(root) is { } responseFormat)
        {
            chat["response_format"] = responseFormat;
        }

        AddIfPresent(root, chat, "store");
        AddIfPresent(root, chat, "frequency_penalty");
        AddIfPresent(root, chat, "presence_penalty");
        AddIfPresent(root, chat, "seed");
        AddIfPresent(root, chat, "user");

        if (root["max_output_tokens"] is { } maxOutputTokens)
        {
            chat["max_tokens"] = maxOutputTokens.DeepClone();
        }
        else
        {
            AddIfPresent(root, chat, "max_tokens");
        }

        chat["messages"] = BuildMessages(root);
        return chat;
    }

    private static void RejectResponsesStatePointers(JsonObject root)
    {
        if (root.ContainsKey("previous_response_id") && root["previous_response_id"] is not null)
        {
            throw new RoutingException(
                "previous_response_id cannot be resolved by a stateless Chat Completions upstream; replay the required Items in input.");
        }
    }

    private static JsonNode? ConvertResponseFormat(JsonObject root)
    {
        if (root["text"] is JsonObject text &&
            text["format"] is { } textFormat)
        {
            return ConvertTextFormat(textFormat);
        }

        return root["response_format"]?.DeepClone();
    }

    private static JsonNode ConvertTextFormat(JsonNode textFormat)
    {
        if (textFormat is not JsonObject formatObject)
        {
            throw new RoutingException("Responses text.format must be an object to downgrade to Chat Completions.");
        }

        string? type = StringValue(formatObject["type"]);
        if (string.Equals(type, "json_schema", StringComparison.OrdinalIgnoreCase))
        {
            if (formatObject["name"] is null || formatObject["schema"] is null)
            {
                throw new RoutingException("Responses text.format json_schema requires name and schema to downgrade to Chat Completions.");
            }

            var jsonSchema = new JsonObject();
            AddIfPresent(formatObject, jsonSchema, "name");
            AddIfPresent(formatObject, jsonSchema, "description");
            AddIfPresent(formatObject, jsonSchema, "strict");
            AddIfPresent(formatObject, jsonSchema, "schema");

            return new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = jsonSchema
            };
        }

        if (string.Equals(type, "json_object", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject { ["type"] = type };
        }

        throw new RoutingException($"Responses text.format type '{type ?? "<missing>"}' cannot be downgraded to Chat Completions.");
    }

    // Responses function tools carry {type:"function", name, description, parameters, strict} flat on the
    // object; Chat Completions nests those under a "function" property. Convert flat tools, pass through any
    // that are already nested (or non-function tool types) unchanged.
    private static JsonNode? ConvertTools(JsonNode? tools)
    {
        if (tools is not JsonArray toolArray)
        {
            return tools?.DeepClone();
        }

        var converted = new JsonArray();
        foreach (JsonNode? tool in toolArray)
        {
            if (tool is JsonObject toolObject &&
                string.Equals(toolObject["type"]?.GetValue<string>(), "function", StringComparison.OrdinalIgnoreCase) &&
                toolObject["function"] is null &&
                toolObject["name"] is not null)
            {
                var function = new JsonObject { ["name"] = toolObject["name"]!.DeepClone() };
                AddIfPresent(toolObject, function, "description");
                AddIfPresent(toolObject, function, "parameters");
                AddIfPresent(toolObject, function, "strict");

                converted.Add(new JsonObject { ["type"] = "function", ["function"] = function });
            }
            else
            {
                converted.Add(tool?.DeepClone());
            }
        }

        return converted;
    }

    // String forms ("auto"/"none"/"required") pass through; an object form {type:"function", name} is nested
    // under "function" to match Chat Completions.
    private static JsonNode? ConvertToolChoice(JsonNode? toolChoice)
    {
        if (toolChoice is JsonObject choiceObject &&
            string.Equals(choiceObject["type"]?.GetValue<string>(), "function", StringComparison.OrdinalIgnoreCase) &&
            choiceObject["function"] is null &&
            choiceObject["name"] is not null)
        {
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = choiceObject["name"]!.DeepClone() }
            };
        }

        return toolChoice?.DeepClone();
    }

    private static void AddIfPresent(JsonObject source, JsonObject target, string name)
    {
        if (source[name] is { } value)
        {
            target[name] = value.DeepClone();
        }
    }

    private static JsonArray BuildMessages(JsonObject root)
    {
        var messages = new JsonArray();

        if (root["instructions"] is { } instructions)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = ScalarToString(instructions)
            });
        }

        if (root["messages"] is JsonArray existingMessages)
        {
            foreach (JsonNode? message in existingMessages)
            {
                JsonNode? clone = message?.DeepClone();
                if (clone is JsonObject messageObject)
                {
                    RemapDeveloperRole(messageObject);
                }

                messages.Add(clone);
            }

            return messages;
        }

        if (root["input"] is null)
        {
            return messages;
        }

        if (root["input"] is JsonValue inputValue)
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = ScalarToString(inputValue)
            });
            return messages;
        }

        if (root["input"] is not JsonArray inputItems)
        {
            return messages;
        }

        AddInputItems(messages, inputItems);

        return messages;
    }

    private static void AddInputItems(JsonArray messages, JsonArray inputItems)
    {
        var consumedToolOutputs = new HashSet<int>();

        for (int i = 0; i < inputItems.Count; i++)
        {
            if (inputItems[i] is not JsonObject itemObject)
            {
                continue;
            }

            string? type = ItemType(itemObject);
            if (string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
            {
                i = AddToolCallRun(messages, inputItems, i, consumedToolOutputs);
                continue;
            }

            if (string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AddNonToolInputItem(messages, itemObject);
        }
    }

    private static int AddToolCallRun(
        JsonArray messages,
        JsonArray inputItems,
        int startIndex,
        HashSet<int> consumedToolOutputs)
    {
        var calls = new List<JsonObject>();
        int endIndex = startIndex;
        while (endIndex < inputItems.Count &&
            inputItems[endIndex] is JsonObject callObject &&
            string.Equals(ItemType(callObject), "function_call", StringComparison.OrdinalIgnoreCase))
        {
            calls.Add(callObject);
            endIndex++;
        }

        var paired = new List<(JsonObject Call, JsonObject Output, int OutputIndex)>();
        foreach (JsonObject call in calls)
        {
            string? callId = StringValue(call["call_id"]);
            if (string.IsNullOrWhiteSpace(callId))
            {
                continue;
            }

            int outputIndex = FindMatchingToolOutput(inputItems, endIndex, callId, consumedToolOutputs);
            if (outputIndex >= 0 && inputItems[outputIndex] is JsonObject output)
            {
                paired.Add((call, output, outputIndex));
            }
        }

        if (paired.Count > 0)
        {
            var toolCalls = new JsonArray();
            foreach ((JsonObject call, _, _) in paired)
            {
                toolCalls.Add(new JsonObject
                {
                    ["id"] = call["call_id"]!.DeepClone(),
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call["name"]?.DeepClone() ?? JsonValue.Create(string.Empty),
                        ["arguments"] = call["arguments"]?.DeepClone() ?? JsonValue.Create("{}")
                    }
                });
            }

            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = toolCalls
            });

            foreach ((_, JsonObject output, int outputIndex) in paired)
            {
                consumedToolOutputs.Add(outputIndex);
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = output["call_id"]!.DeepClone(),
                    ["content"] = ScalarToString(output["output"])
                });
            }
        }

        return endIndex - 1;
    }

    private static int FindMatchingToolOutput(
        JsonArray inputItems,
        int startIndex,
        string callId,
        HashSet<int> consumedToolOutputs)
    {
        // Only function_call / function_call_output / message Items gate pairing. reasoning and
        // hosted-tool Items are intentionally skipped here (they are removed from the downgraded
        // request, so they cannot sit between a call and its output in the emitted Chat transcript) —
        // a tool output may still be paired with its call across them. Messages and a following
        // function_call do close the pairing window (they start a new turn / run).
        for (int i = startIndex; i < inputItems.Count; i++)
        {
            if (inputItems[i] is not JsonObject item)
            {
                continue;
            }

            string? type = ItemType(item);
            if (string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase))
            {
                if (!consumedToolOutputs.Contains(i) &&
                    string.Equals(StringValue(item["call_id"]), callId, StringComparison.Ordinal))
                {
                    return i;
                }

                continue;
            }

            if (string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase) ||
                IsMessageItem(item))
            {
                return -1;
            }
        }

        return -1;
    }

    private static void AddNonToolInputItem(JsonArray messages, JsonObject item)
    {
        string? type = ItemType(item);

        if (string.Equals(type, "reasoning", StringComparison.OrdinalIgnoreCase) ||
            IsHostedToolItem(type))
        {
            return;
        }

        if (!IsMessageItem(item))
        {
            throw new RoutingException($"Responses input item type '{type ?? "<missing>"}' cannot be downgraded to Chat Completions.");
        }

        string role = ToChatRole(StringValue(item["role"]));
        JsonNode? content = item["content"];
        messages.Add(new JsonObject
        {
            ["role"] = role,
            ["content"] = ConvertMessageContent(content)
        });
    }

    private static bool IsMessageItem(JsonObject item)
    {
        string? type = ItemType(item);
        return type is null ||
            string.Equals(type, "message", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHostedToolItem(string? type) =>
        type is not null &&
        (type.EndsWith("_call", StringComparison.OrdinalIgnoreCase) ||
            type.EndsWith("_call_output", StringComparison.OrdinalIgnoreCase)) &&
        !string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase);

    private static string? ItemType(JsonObject item) => StringValue(item["type"]);

    private static string? StringValue(JsonNode? value) =>
        value is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.String
            ? jsonValue.GetValue<string>()
            : null;

    // Moonshot/kimi and some OpenAI-compatible Chat upstreams reject the OpenAI "developer" role with
    // "tokenization failed" — their chat template only knows system/user/assistant/tool. "developer" is
    // OpenAI's successor to "system", so fold it back to "system" on the Chat Completions wire. This only
    // runs inside ToChatCompletions (chat_completions providers); a real /responses upstream keeps "developer".
    private static string ToChatRole(string? role) =>
        string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase) ? "system" : role ?? "user";

    private static void RemapDeveloperRole(JsonObject message)
    {
        if (message["role"] is JsonValue role &&
            role.GetValueKind() == JsonValueKind.String &&
            string.Equals(role.GetValue<string>(), "developer", StringComparison.OrdinalIgnoreCase))
        {
            message["role"] = "system";
        }
    }

    private static JsonNode? ConvertMessageContent(JsonNode? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        if (content is JsonValue contentValue)
        {
            return ScalarToString(contentValue);
        }

        if (content is not JsonArray contentItems)
        {
            return content.DeepClone();
        }

        var chatContent = new JsonArray();
        foreach (JsonNode? item in contentItems)
        {
            if (item is not JsonObject contentObject)
            {
                continue;
            }

            string? type = contentObject["type"]?.GetValue<string>();
            if (string.Equals(type, "input_text", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
            {
                chatContent.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = ScalarToString(contentObject["text"])
                });
            }
            else if (string.Equals(type, "input_image", StringComparison.OrdinalIgnoreCase) &&
                contentObject["image_url"] is { } imageUrl)
            {
                chatContent.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject
                    {
                        ["url"] = imageUrl.DeepClone()
                    }
                });
            }
        }

        if (chatContent.Count == 1 &&
            chatContent[0] is JsonObject only &&
            only["type"]?.GetValue<string>() == "text")
        {
            return only["text"]?.DeepClone() ?? string.Empty;
        }

        return chatContent;
    }

    private static string ScalarToString(JsonNode? value) =>
        value is null ? string.Empty : value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();

    private static JsonObject ParseObject(string body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject
                ?? throw new RoutingException("Request body must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new RoutingException($"Request body is not valid JSON: {ex.Message}");
        }
    }
}
