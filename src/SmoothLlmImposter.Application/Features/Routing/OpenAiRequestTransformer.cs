using System.Text.Json;
using System.Text.Json.Nodes;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// OpenAI-dialect transform: rewrites <c>model</c> and, when caching is enabled, sets
/// <c>prompt_cache_key</c> to the original (inbound) model so requests for the same imposter model
/// share an upstream cache bucket. OpenAI caches automatically, so no content restructuring is needed.
/// </summary>
internal sealed class OpenAiRequestTransformer : IRequestTransformer
{
    public ApiDialect Dialect => ApiDialect.OpenAi;

    public string Transform(string requestBody, RouteDecision decision, string inboundModel)
    {
        JsonObject root = ParseObject(requestBody);

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
        AddIfPresent(root, chat, "response_format");
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
                messages.Add(message?.DeepClone());
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

        foreach (JsonNode? item in inputItems)
        {
            if (item is JsonObject itemObject)
            {
                AddInputItem(messages, itemObject);
            }
        }

        return messages;
    }

    private static void AddInputItem(JsonArray messages, JsonObject item)
    {
        string? type = item["type"]?.GetValue<string>();

        if (string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = item["call_id"]?.DeepClone() ?? JsonValue.Create(string.Empty),
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = item["name"]?.DeepClone() ?? JsonValue.Create(string.Empty),
                            ["arguments"] = item["arguments"]?.DeepClone() ?? JsonValue.Create("{}")
                        }
                    }
                }
            });
            return;
        }

        if (string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = item["call_id"]?.DeepClone() ?? JsonValue.Create(string.Empty),
                ["content"] = ScalarToString(item["output"])
            });
            return;
        }

        string role = item["role"]?.GetValue<string>() ?? "user";
        JsonNode? content = item["content"];
        messages.Add(new JsonObject
        {
            ["role"] = role,
            ["content"] = ConvertMessageContent(content)
        });
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
