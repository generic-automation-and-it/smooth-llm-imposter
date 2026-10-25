using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing.Bridge;

/// <summary>
/// Maps an inbound Anthropic Messages request to a Chat Completions request (HLD 016 spec 01).
/// Pure function of (body, inbound model name, provider options); the output is byte-stable.
/// </summary>
internal sealed class AnthropicChatRequestMapper(ILogger<AnthropicChatRequestMapper> logger)
{
    private const int MinOutputTokens = 16;

    public AnthropicChatRequestResult Map(
        string messagesBody,
        string inboundModel,
        AnthropicChatBridgeOptions options)
    {
        JsonObject root = AnthropicChatJson.TryParseObject(messagesBody)
            ?? throw new AnthropicChatRequestException("Request body must be a JSON object.");

        string model = RequireModel(root);
        int maxTokens = RequireMaxTokens(root);
        bool stream = ValidateStream(root);
        ValidateNumbers(root);
        string[]? stop = ValidateStopSequences(root);
        ValidateSystem(root);

        var identity = new AnthropicChatToolIdentity();
        ToolCatalog tools = ClassifyTools(root["tools"], identity, options);
        ToolChoice toolChoice = ValidateToolChoice(root["tool_choice"], tools, identity);

        Conversion conversion = ConvertMessages(root["messages"], identity, options);
        if (!conversion.HasMessages)
        {
            throw new AnthropicChatRequestException("messages: no message contains usable content");
        }

        string? systemText = BuildSystemText(root["system"], options);
        JsonArray chatMessages = conversion.Messages;
        if (systemText is not null)
        {
            chatMessages.Insert(0, (JsonNode)SystemMessage(systemText, root["system"], options));
        }

        JsonObject body = AssembleBody(root, model, maxTokens, stream, stop, tools, toolChoice, chatMessages, options);

        int estimate = AnthropicChatTokenEstimator.RequestEstimate(root, options);

        return new AnthropicChatRequestResult(
            AnthropicChatJson.CompactObject(body),
            new AnthropicChatBridgeContext(inboundModel, stream, identity, estimate, options));
    }

    // ---------------------------------------------------------------- top-level validation

    private static string RequireModel(JsonObject root)
    {
        JsonNode? node = root["model"];
        if (node is null)
        {
            throw new AnthropicChatRequestException("model: Field required");
        }

        string? model = AnthropicChatJson.String(node);
        if (string.IsNullOrEmpty(model))
        {
            throw new AnthropicChatRequestException("model: Input should be a valid string");
        }

        return model;
    }

    private static int RequireMaxTokens(JsonObject root)
    {
        JsonNode? node = root["max_tokens"];
        if (node is null)
        {
            throw new AnthropicChatRequestException("max_tokens: Field required");
        }

        if (!AnthropicChatJson.TryGetInteger(node, 1, out int value))
        {
            throw new AnthropicChatRequestException("max_tokens: Input should be a valid integer greater than or equal to 1");
        }

        return value;
    }

    private static bool ValidateStream(JsonObject root)
    {
        JsonNode? node = root["stream"];
        if (node is null)
        {
            return false;
        }

        bool? stream = AnthropicChatJson.Bool(node);
        return stream ?? throw new AnthropicChatRequestException("stream: Input should be a valid boolean");
    }

    private static void ValidateNumbers(JsonObject root)
    {
        foreach (string field in (string[])["temperature", "top_p"])
        {
            JsonNode? node = root[field];
            if (node is not null && AnthropicChatJson.Number(node) is null)
            {
                throw new AnthropicChatRequestException($"{field}: Input should be a valid number");
            }
        }
    }

    private static string[]? ValidateStopSequences(JsonObject root)
    {
        JsonNode? node = root["stop_sequences"];
        if (node is null)
        {
            return null;
        }

        if (node is not JsonArray array)
        {
            throw new AnthropicChatRequestException("stop_sequences: Input should be a valid list");
        }

        var values = new string[array.Count];
        for (int i = 0; i < array.Count; i++)
        {
            values[i] = AnthropicChatJson.String(array[i])
                ?? throw new AnthropicChatRequestException($"stop_sequences.{i}: Input should be a valid string");
        }

        return values;
    }

    private static void ValidateSystem(JsonObject root)
    {
        JsonNode? node = root["system"];
        if (node is null)
        {
            return;
        }

        if (AnthropicChatJson.String(node) is not null || node is JsonArray)
        {
            return;
        }

        throw new AnthropicChatRequestException("system: Input should be a valid string or list");
    }

    // ---------------------------------------------------------------- tools

    private sealed record ToolCatalog(JsonArray Converted, HashSet<string> SurvivingNames);

    private sealed record ToolChoice(JsonNode? Output, bool ParallelOff);

    private ToolCatalog ClassifyTools(JsonNode? toolsNode, AnthropicChatToolIdentity identity, AnthropicChatBridgeOptions options)
    {
        var converted = new JsonArray();
        var surviving = new HashSet<string>(StringComparer.Ordinal);

        if (toolsNode is null)
        {
            return new ToolCatalog(converted, surviving);
        }

        if (toolsNode is not JsonArray tools)
        {
            throw new AnthropicChatRequestException("tools: Input should be a valid list");
        }

        for (int i = 0; i < tools.Count; i++)
        {
            if (tools[i] is not JsonObject tool)
            {
                throw new AnthropicChatRequestException($"tools.{i}: Input should be a valid dictionary");
            }

            string? type = AnthropicChatJson.String(tool["type"]);
            if (type is not null && !string.Equals(type, "custom", StringComparison.OrdinalIgnoreCase))
            {
                if (IsToolSearchType(type))
                {
                    logger.LogDebug("Dropped tool-search tool definition at index {Index} ({Type})", i, type);
                    continue;
                }

                throw new AnthropicChatRequestException($"tools.{i}.type: \"{type}\" server tools are not supported");
            }

            string? name = AnthropicChatJson.String(tool["name"]);
            if (string.IsNullOrEmpty(name))
            {
                throw new AnthropicChatRequestException($"tools.{i}.name: Field required");
            }

            JsonNode? schemaNode = tool["input_schema"];
            if (schemaNode is not null && schemaNode is not JsonObject)
            {
                throw new AnthropicChatRequestException($"tools.{i}.input_schema: Input should be a valid dictionary");
            }

            if (!surviving.Add(name))
            {
                logger.LogDebug("Dropped duplicate tool definition '{Name}' at index {Index}", name, i);
                continue;
            }

            identity.RegisterName(name);
            converted.Add((JsonNode)ConvertTool(tool, identity.TargetFor(name), schemaNode, options));
        }

        return new ToolCatalog(converted, surviving);
    }

    private static bool IsToolSearchType(string type) =>
        string.Equals(type, "tool_search", StringComparison.OrdinalIgnoreCase) ||
        type.StartsWith("tool_search_tool_", StringComparison.OrdinalIgnoreCase);

    private static JsonObject ConvertTool(JsonObject source, string targetName, JsonNode? schemaNode, AnthropicChatBridgeOptions options)
    {
        var function = new JsonObject
        {
            ["name"] = targetName,
            ["parameters"] = schemaNode is JsonObject schema
                ? schema.DeepClone()
                : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
        };

        if (AnthropicChatJson.String(source["description"]) is { Length: > 0 } description)
        {
            function["description"] = description;
        }

        if (source["strict"] is JsonValue strictValue && strictValue.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
        {
            function["strict"] = strictValue.DeepClone();
        }

        var tool = new JsonObject
        {
            ["type"] = "function",
            ["function"] = function
        };

        if (options.ForwardCacheControl && source["cache_control"] is JsonObject cacheControl)
        {
            tool["cache_control"] = cacheControl.DeepClone();
        }

        return tool;
    }

    private ToolChoice ValidateToolChoice(JsonNode? choiceNode, ToolCatalog tools, AnthropicChatToolIdentity identity)
    {
        if (choiceNode is null)
        {
            return new ToolChoice(null, false);
        }

        bool anyTools = tools.SurvivingNames.Count > 0;

        if (AnthropicChatJson.String(choiceNode) is { } choiceString)
        {
            string normalized = choiceString switch
            {
                "auto" => "auto",
                "none" => "none",
                "any" or "required" => "required",
                _ => throw new AnthropicChatRequestException("tool_choice: Input should be 'auto', 'any', 'tool' or 'none'")
            };

            if (!anyTools && normalized is "auto" or "none")
            {
                return new ToolChoice(null, false);
            }

            if (!anyTools)
            {
                throw new AnthropicChatRequestException($"tool_choice: \"{normalized}\" requires at least one tool");
            }

            return new ToolChoice(normalized, false);
        }

        if (choiceNode is not JsonObject choice)
        {
            throw new AnthropicChatRequestException("tool_choice: Input should be a valid dictionary");
        }

        string? type = AnthropicChatJson.String(choice["type"]);
        if (type is not ("auto" or "any" or "tool" or "none"))
        {
            throw new AnthropicChatRequestException("tool_choice.type: Input should be 'auto', 'any', 'tool' or 'none'");
        }

        bool parallelOff = AnthropicChatJson.Bool(choice["disable_parallel_tool_use"]) == true;

        if (type == "tool")
        {
            string? name = AnthropicChatJson.String(choice["name"]);
            if (string.IsNullOrEmpty(name))
            {
                throw new AnthropicChatRequestException("tool_choice.name: Field required");
            }

            if (!anyTools)
            {
                throw new AnthropicChatRequestException("tool_choice: \"tool\" requires at least one tool");
            }

            if (!tools.SurvivingNames.Contains(name))
            {
                throw new AnthropicChatRequestException($"tool_choice.name: no tool named \"{name}\" in tools");
            }

            var forced = new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = identity.TargetFor(name) }
            };

            return new ToolChoice(forced, parallelOff && anyTools);
        }

        if (type == "none")
        {
            return new ToolChoice("none", false);
        }

        if (!anyTools)
        {
            if (type == "auto")
            {
                return new ToolChoice(null, false);
            }

            throw new AnthropicChatRequestException($"tool_choice: \"{type}\" requires at least one tool");
        }

        string normalizedChoice = type == "any" ? "required" : type;
        return new ToolChoice(normalizedChoice, parallelOff);
    }

    // ---------------------------------------------------------------- messages

    private sealed record Conversion(JsonArray Messages, bool HasMessages);

    private Conversion ConvertMessages(JsonNode? messagesNode, AnthropicChatToolIdentity identity, AnthropicChatBridgeOptions options)
    {
        if (messagesNode is null)
        {
            throw new AnthropicChatRequestException("messages: Field required");
        }

        if (messagesNode is not JsonArray messages)
        {
            throw new AnthropicChatRequestException("messages: Input should be a valid list");
        }

        if (messages.Count == 0)
        {
            throw new AnthropicChatRequestException("messages: at least one message is required");
        }

        var output = new JsonArray();
        var state = new ConversionState();

        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject message)
            {
                throw new AnthropicChatRequestException($"messages.{i}: Input should be a valid dictionary");
            }

            string? role = AnthropicChatJson.String(message["role"]);
            if (role is not ("user" or "assistant" or "system"))
            {
                throw new AnthropicChatRequestException($"messages.{i}.role: Input should be 'user', 'assistant' or 'system'");
            }

            JsonNode? content = message["content"];
            if (content is null)
            {
                throw new AnthropicChatRequestException($"messages.{i}.content: Field required");
            }

            if (AnthropicChatJson.String(content) is null && content is not JsonArray)
            {
                throw new AnthropicChatRequestException($"messages.{i}.content: Input should be a valid string or list");
            }

            switch (role)
            {
                case "system":
                    ConvertMidturnSystem(content, i, state, output, options);
                    break;
                case "assistant":
                    ConvertAssistantMessage(content, i, identity, options, output, state);
                    break;
                case "user":
                    ConvertUserMessage(content, i, identity, options, output, state);
                    break;
            }
        }

        if (!string.IsNullOrEmpty(state.PendingMerge))
        {
            output.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = state.PendingMerge });
        }

        return new Conversion(output, output.Count > 0);
    }

    private sealed class ConversionState
    {
        public JsonObject? Previous { get; set; }

        public string PendingMerge { get; set; } = string.Empty;
    }

    private static void ConvertMidturnSystem(
        JsonNode? content,
        int messageIndex,
        ConversionState state,
        JsonArray output,
        AnthropicChatBridgeOptions options)
    {
        string text = ContentToText(content, messageIndex);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (options.MidturnSystem == AnthropicChatMidturnSystem.System)
        {
            var message = new JsonObject { ["role"] = "system", ["content"] = text };
            output.Add((JsonNode)message);
            state.Previous = message;
            return;
        }

        state.PendingMerge = string.IsNullOrEmpty(state.PendingMerge) ? text : state.PendingMerge + "\n" + text;
    }

    private void ConvertAssistantMessage(
        JsonNode? content,
        int messageIndex,
        AnthropicChatToolIdentity identity,
        AnthropicChatBridgeOptions options,
        JsonArray output,
        ConversionState state)
    {
        var textItems = new List<string>();
        var toolCalls = new JsonArray();
        var thinkingItems = new List<string>();

        if (AnthropicChatJson.String(content) is { } stringContent)
        {
            if (!AnthropicChatJson.IsWhiteSpaceOnly(stringContent))
            {
                textItems.Add(stringContent);
            }
        }
        else if (content is JsonArray blocks)
        {
            for (int j = 0; j < blocks.Count; j++)
            {
                JsonObject block = RequireBlock(blocks[j], messageIndex, j);
                string type = RequireBlockType(block, messageIndex, j);

                switch (type)
                {
                    case "text":
                        if (AnthropicChatJson.String(block["text"]) is { } blockText &&
                            !AnthropicChatJson.IsWhiteSpaceOnly(blockText))
                        {
                            textItems.Add(blockText);
                        }
                        break;

                    case "tool_use":
                        toolCalls.Add((JsonNode)ConvertToolUse(block, messageIndex, j, identity));
                        break;

                    case "thinking":
                        if (AnthropicChatJson.String(block["thinking"]) is { } thinking &&
                            !AnthropicChatJson.IsWhiteSpaceOnly(thinking))
                        {
                            thinkingItems.Add(thinking);
                        }
                        break;

                    case "redacted_thinking":
                        break;

                    case "image":
                        break;

                    case "document" when options.DocumentMode == AnthropicChatDocumentMode.Image &&
                        AnthropicChatTokenEstimator.IsImageDocument(block):
                        // A document sent as an image part in an assistant turn is dropped like an image (D15).
                        break;

                    default:
                        logger.LogDebug("Replaced unsupported block '{Type}' at messages.{Index}.content.{Block}",
                            type, messageIndex, j);
                        textItems.Add(AnthropicChatJson.Placeholder(type));
                        break;
                }
            }
        }

        string text = string.Join('\n', textItems);
        bool hasReasoning = thinkingItems.Count > 0 &&
            options.CompatProfile is AnthropicChatCompatProfile.DeepSeek
                or AnthropicChatCompatProfile.Mimo
                or AnthropicChatCompatProfile.MiniMax;

        if (text.Length == 0 && toolCalls.Count == 0 && !hasReasoning)
        {
            return;
        }

        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = text
        };

        if (toolCalls.Count > 0)
        {
            message["tool_calls"] = toolCalls;
        }

        if (hasReasoning)
        {
            switch (options.CompatProfile)
            {
                case AnthropicChatCompatProfile.DeepSeek:
                case AnthropicChatCompatProfile.Mimo:
                    message["reasoning_content"] = string.Join('\n', thinkingItems);
                    break;

                case AnthropicChatCompatProfile.MiniMax:
                {
                    var details = new JsonArray();
                    for (int i = 0; i < thinkingItems.Count; i++)
                    {
                        details.Add((JsonNode)new JsonObject
                        {
                            ["type"] = "reasoning.text",
                            ["index"] = i,
                            ["text"] = thinkingItems[i]
                        });
                    }

                    message["reasoning_details"] = details;
                    break;
                }
            }
        }

        output.Add((JsonNode)message);
        state.Previous = message;
    }

    private JsonObject ConvertToolUse(JsonObject block, int messageIndex, int blockIndex, AnthropicChatToolIdentity identity)
    {
        string? id = AnthropicChatJson.String(block["id"]);
        if (string.IsNullOrEmpty(id))
        {
            throw new AnthropicChatRequestException($"messages.{messageIndex}.content.{blockIndex}.id: Field required");
        }

        string? name = AnthropicChatJson.String(block["name"]);
        if (string.IsNullOrEmpty(name))
        {
            throw new AnthropicChatRequestException($"messages.{messageIndex}.content.{blockIndex}.name: Field required");
        }

        JsonNode? input = block["input"];
        if (input is not null && input is not JsonObject)
        {
            throw new AnthropicChatRequestException($"messages.{messageIndex}.content.{blockIndex}.input: Input should be a valid dictionary");
        }

        identity.RegisterName(name);

        return new JsonObject
        {
            ["id"] = identity.MapToolId(id),
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = identity.TargetFor(name),
                ["arguments"] = input is JsonObject inputObject
                    ? AnthropicChatJson.Compact(inputObject)
                    : "{}"
            }
        };
    }

    private void ConvertUserMessage(
        JsonNode? content,
        int messageIndex,
        AnthropicChatToolIdentity identity,
        AnthropicChatBridgeOptions options,
        JsonArray output,
        ConversionState state)
    {
        var items = new List<UserItem>();

        if (AnthropicChatJson.String(content) is { } stringContent)
        {
            if (!AnthropicChatJson.IsWhiteSpaceOnly(stringContent))
            {
                items.Add(UserItem.FromText(stringContent));
            }
        }
        else if (content is JsonArray blocks)
        {
            for (int j = 0; j < blocks.Count; j++)
            {
                JsonObject block = RequireBlock(blocks[j], messageIndex, j);
                string type = RequireBlockType(block, messageIndex, j);

                switch (type)
                {
                    case "text":
                        if (AnthropicChatJson.String(block["text"]) is { } text &&
                            !AnthropicChatJson.IsWhiteSpaceOnly(text))
                        {
                            JsonObject? textCache = options.ForwardCacheControl && block["cache_control"] is JsonObject tc
                                ? tc
                                : null;
                            items.Add(UserItem.FromText(text, textCache));
                        }
                        break;

                    case "image":
                        items.Add(UserItem.FromImage(BuildImagePart(block, messageIndex, j, options)));
                        break;

                    case "document" when options.DocumentMode == AnthropicChatDocumentMode.Image &&
                        AnthropicChatTokenEstimator.IsImageDocument(block):
                        items.Add(UserItem.FromImage(BuildDocumentImagePart(block, messageIndex, j, options)));
                        break;

                    case "tool_result":
                        items.Add(UserItem.FromToolResult(ParseToolResult(block, messageIndex, j, identity, options)));
                        break;

                    case "thinking":
                    case "redacted_thinking":
                        break;

                    default:
                        logger.LogDebug("Replaced unsupported block '{Type}' at messages.{Index}.content.{Block}",
                            type, messageIndex, j);
                        items.Add(UserItem.FromText(AnthropicChatJson.Placeholder(type)));
                        break;
                }
            }
        }

        // D14: match this user message's tool results against the immediately preceding assistant message.
        var pendingCallIds = new List<string>();
        if (state.Previous is { } previous &&
            string.Equals(AnthropicChatJson.String(previous["role"]), "assistant", StringComparison.Ordinal) &&
            previous["tool_calls"] is JsonArray previousCalls)
        {
            foreach (JsonNode? callNode in previousCalls)
            {
                if (callNode is JsonObject call && AnthropicChatJson.String(call["id"]) is { } callId)
                {
                    pendingCallIds.Add(callId);
                }
            }
        }

        var consumed = new List<ToolResultItem>();
        foreach (string callId in pendingCallIds)
        {
            UserItem? match = null;
            foreach (UserItem item in items)
            {
                if (item.Kind == UserItemKind.ToolResult &&
                    !item.ToolResultValue!.Consumed &&
                    string.Equals(item.ToolResultValue!.MappedId, callId, StringComparison.Ordinal))
                {
                    match = item;
                    break;
                }
            }

            if (match is null)
            {
                continue;
            }

            match.ToolResultValue!.Consumed = true;
            consumed.Add(match.ToolResultValue!);
            var toolMessage = new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = match.ToolResultValue!.MappedId,
                ["content"] = match.ToolResultValue!.Text
            };

            if (options.ForwardCacheControl && match.ToolResultValue!.CacheControl is { } cacheControl)
            {
                toolMessage["cache_control"] = cacheControl.DeepClone();
            }

            output.Add((JsonNode)toolMessage);
            state.Previous = toolMessage;
        }

        foreach (ToolResultItem result in consumed)
        {
            if (result.Images.Count == 0)
            {
                continue;
            }

            var contentParts = new JsonArray
            {
                (JsonNode)new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"Images returned by tool call {result.MappedId}:"
                }
            };

            foreach (JsonObject image in result.Images)
            {
                contentParts.Add((JsonNode)image);
            }

            var hoisted = new JsonObject { ["role"] = "user", ["content"] = contentParts };
            output.Add((JsonNode)hoisted);
            state.Previous = hoisted;
        }

        JsonObject? sibling = BuildSiblingUserMessage(items, state.PendingMerge);
        if (sibling is null)
        {
            if (!string.IsNullOrEmpty(state.PendingMerge))
            {
                sibling = new JsonObject { ["role"] = "user", ["content"] = state.PendingMerge };
            }
        }
        else
        {
            state.PendingMerge = string.Empty;
        }

        if (sibling is not null)
        {
            state.PendingMerge = string.Empty;
            output.Add((JsonNode)sibling);
            state.Previous = sibling;
        }
    }

    private static JsonObject? BuildSiblingUserMessage(List<UserItem> items, string pendingMerge)
    {
        var parts = new List<JsonObject>();
        if (!string.IsNullOrEmpty(pendingMerge))
        {
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = pendingMerge });
        }

        int index = 0;
        while (index < items.Count)
        {
            UserItem item = items[index];
            switch (item.Kind)
            {
                case UserItemKind.Text:
                {
                    var builder = new StringBuilder(item.TextValue);
                    JsonObject? cacheControl = item.CacheControl;
                    index++;
                    while (index < items.Count && items[index].Kind == UserItemKind.Text)
                    {
                        builder.Append('\n').Append(items[index].TextValue);
                        cacheControl = items[index].CacheControl ?? cacheControl;
                        index++;
                    }

                    AddOrMergeTextPart(parts, builder.ToString(), cacheControl);
                    break;
                }

                case UserItemKind.Image:
                    parts.Add(item.ImageValue!);
                    index++;
                    break;

                case UserItemKind.ToolResult:
                    if (item.ToolResultValue!.Consumed)
                    {
                        index++;
                        break;
                    }

                    string orphanText = $"Tool result (tool_use_id: {item.ToolResultValue.OriginalId}):";
                    if (item.ToolResultValue.Text.Length > 0)
                    {
                        orphanText += "\n" + item.ToolResultValue.Text;
                    }

                    AddOrMergeTextPart(parts, orphanText, null);
                    foreach (JsonObject image in item.ToolResultValue.Images)
                    {
                        parts.Add(image);
                    }
                    index++;
                    break;
            }
        }

        if (parts.Count == 0)
        {
            return null;
        }

        bool singlePlainText = parts.Count == 1 &&
            string.Equals(AnthropicChatJson.String(parts[0]["type"]), "text", StringComparison.Ordinal) &&
            parts[0]["cache_control"] is null;
        JsonNode content = singlePlainText
            ? JsonValue.Create(AnthropicChatJson.String(parts[0]["text"]) ?? string.Empty)
            : new JsonArray(parts.ToArray());

        return new JsonObject { ["role"] = "user", ["content"] = content };
    }

    private static void AddOrMergeTextPart(List<JsonObject> parts, string text, JsonObject? cacheControl)
    {
        if (parts.Count > 0 &&
            string.Equals(AnthropicChatJson.String(parts[^1]["type"]), "text", StringComparison.Ordinal))
        {
            JsonObject last = parts[^1];
            last["text"] = (AnthropicChatJson.String(last["text"]) ?? string.Empty) + "\n" + text;
            if (cacheControl is not null)
            {
                last["cache_control"] = cacheControl.DeepClone();
            }

            return;
        }

        var part = new JsonObject { ["type"] = "text", ["text"] = text };
        if (cacheControl is not null)
        {
            part["cache_control"] = cacheControl.DeepClone();
        }

        parts.Add(part);
    }

    private sealed class UserItem
    {
        public UserItemKind Kind { get; private init; }

        public string TextValue { get; private init; } = string.Empty;

        public JsonObject? ImageValue { get; private init; }

        public ToolResultItem? ToolResultValue { get; private init; }

        public JsonObject? CacheControl { get; private init; }

        public static UserItem FromText(string text, JsonObject? cacheControl = null) =>
            new() { Kind = UserItemKind.Text, TextValue = text, CacheControl = cacheControl };

        public static UserItem FromImage(JsonObject image) => new() { Kind = UserItemKind.Image, ImageValue = image };

        public static UserItem FromToolResult(ToolResultItem result) => new() { Kind = UserItemKind.ToolResult, ToolResultValue = result };
    }

    private enum UserItemKind
    {
        Text,
        Image,
        ToolResult
    }

    private sealed class ToolResultItem
    {
        public string OriginalId { get; init; } = string.Empty;

        public string MappedId { get; init; } = string.Empty;

        public string Text { get; init; } = string.Empty;

        public List<JsonObject> Images { get; init; } = [];

        public JsonObject? CacheControl { get; init; }

        public bool Consumed { get; set; }
    }

    private ToolResultItem ParseToolResult(
        JsonObject block,
        int messageIndex,
        int blockIndex,
        AnthropicChatToolIdentity identity,
        AnthropicChatBridgeOptions options)
    {
        string? toolUseId = AnthropicChatJson.String(block["tool_use_id"]);
        if (string.IsNullOrEmpty(toolUseId))
        {
            throw new AnthropicChatRequestException($"messages.{messageIndex}.content.{blockIndex}.tool_use_id: Field required");
        }

        JsonNode? content = block["content"];
        if (AnthropicChatJson.String(content) is null && content is not null && content is not JsonArray)
        {
            throw new AnthropicChatRequestException($"messages.{messageIndex}.content.{blockIndex}.content: Input should be a valid string or list");
        }

        (string text, List<JsonObject> images) = ConveyToolResult(content, messageIndex, blockIndex, identity, options);

        if (AnthropicChatJson.Bool(block["is_error"]) == true)
        {
            text = text.Length == 0 ? "Error: (no details)" : "Error: " + text;
        }

        JsonObject? cacheControl = options.ForwardCacheControl && block["cache_control"] is JsonObject cc ? cc : null;

        return new ToolResultItem
        {
            OriginalId = toolUseId,
            MappedId = identity.MapResultToolId(toolUseId),
            Text = text,
            Images = images,
            CacheControl = cacheControl
        };
    }

    private (string Text, List<JsonObject> Images) ConveyToolResult(
        JsonNode? content,
        int messageIndex,
        int blockIndex,
        AnthropicChatToolIdentity identity,
        AnthropicChatBridgeOptions options)
    {
        if (AnthropicChatJson.String(content) is { } stringContent)
        {
            return (stringContent, []);
        }

        if (content is not JsonArray blocks)
        {
            return (string.Empty, []);
        }

        var lines = new List<string>();
        var images = new List<JsonObject>();
        var references = new List<string>();

        for (int k = 0; k < blocks.Count; k++)
        {
            JsonObject nested = RequireBlock(blocks[k], messageIndex, blockIndex, k);
            string type = RequireBlockType(nested, messageIndex, blockIndex, k);

            switch (type)
            {
                case "text":
                    if (AnthropicChatJson.String(nested["text"]) is { } text &&
                        !AnthropicChatJson.IsWhiteSpaceOnly(text))
                    {
                        lines.Add(text);
                    }
                    break;

                case "image":
                    images.Add(BuildImagePart(nested, messageIndex, blockIndex, options, k));
                    break;

                case "document" when options.DocumentMode == AnthropicChatDocumentMode.Image &&
                    AnthropicChatTokenEstimator.IsImageDocument(nested):
                    images.Add(BuildDocumentImagePart(nested, messageIndex, blockIndex, options, k));
                    break;

                case "tool_reference":
                    if (AnthropicChatJson.String(nested["tool_name"]) is { } toolName)
                    {
                        string mapped = identity.TargetFor(toolName);
                        if (!references.Contains(mapped))
                        {
                            references.Add(mapped);
                        }
                    }
                    break;

                default:
                    logger.LogDebug("Replaced unsupported nested block '{Type}' at messages.{Index}.content.{Block}.content.{Nested}",
                        type, messageIndex, blockIndex, k);
                    lines.Add(AnthropicChatJson.Placeholder(type));
                    break;
            }
        }

        string conveyed = string.Join('\n', lines);
        if (references.Count > 0)
        {
            var referenceArray = new JsonArray();
            foreach (string reference in references)
            {
                referenceArray.Add((JsonNode)new JsonObject
                {
                    ["type"] = "tool_reference",
                    ["tool_name"] = reference
                });
            }

            string referenceText = AnthropicChatJson.Compact(referenceArray);
            conveyed = conveyed.Length == 0 ? referenceText : conveyed + "\n" + referenceText;
        }

        if (conveyed.Length == 0 && images.Count > 0)
        {
            conveyed = "(tool returned an image; see the next message)";
        }

        return (conveyed, images);
    }

    private JsonObject BuildImagePart(JsonObject block, int messageIndex, int blockIndex, AnthropicChatBridgeOptions options, int nestedIndex = -1)
    {
        string path = nestedIndex >= 0
            ? $"messages.{messageIndex}.content.{blockIndex}.content.{nestedIndex}"
            : $"messages.{messageIndex}.content.{blockIndex}";

        if (block["source"] is not JsonObject source)
        {
            throw new AnthropicChatRequestException($"{path}.source: Field required");
        }

        string? sourceType = AnthropicChatJson.String(source["type"]);
        if (sourceType is not ("base64" or "url"))
        {
            throw new AnthropicChatRequestException($"{path}.source.type: Input should be 'base64' or 'url'");
        }

        if (sourceType == "base64")
        {
            string? mediaType = AnthropicChatJson.String(source["media_type"]);
            if (mediaType is null || !mediaType.ToLowerInvariant().StartsWith("image/", StringComparison.Ordinal))
            {
                throw new AnthropicChatRequestException($"{path}.source.media_type: Input should be an image media type");
            }

            string? data = AnthropicChatJson.String(source["data"]);
            if (string.IsNullOrEmpty(data))
            {
                throw new AnthropicChatRequestException($"{path}.source.data: Field required");
            }

            var part = new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = $"data:{mediaType.ToLowerInvariant()};base64,{data}"
                }
            };
            ApplyPartCacheControl(part, block, options);
            return part;
        }

        string? url = AnthropicChatJson.String(source["url"]);
        if (string.IsNullOrEmpty(url))
        {
            throw new AnthropicChatRequestException($"{path}.source.url: Field required");
        }

        var urlPart = new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject { ["url"] = url }
        };
        ApplyPartCacheControl(urlPart, block, options);
        return urlPart;
    }

    private static void ApplyPartCacheControl(JsonObject part, JsonObject source, AnthropicChatBridgeOptions options)
    {
        if (options.ForwardCacheControl && source["cache_control"] is JsonObject cacheControl)
        {
            part["cache_control"] = cacheControl.DeepClone();
        }
    }

    private static JsonObject BuildDocumentImagePart(JsonObject block, int messageIndex, int blockIndex, AnthropicChatBridgeOptions options, int nestedIndex = -1)
    {
        JsonObject source = (JsonObject)block["source"]!;
        string path = nestedIndex >= 0
            ? $"messages.{messageIndex}.content.{blockIndex}.content.{nestedIndex}"
            : $"messages.{messageIndex}.content.{blockIndex}";

        if (string.Equals(AnthropicChatJson.String(source["type"]), "base64", StringComparison.Ordinal))
        {
            string mediaType = (AnthropicChatJson.String(source["media_type"]) ?? string.Empty).ToLowerInvariant();
            string data = AnthropicChatJson.String(source["data"]) ?? string.Empty;
            var documentPart = new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{mediaType};base64,{data}" }
            };
            ApplyPartCacheControl(documentPart, block, options);
            return documentPart;
        }

        string url = AnthropicChatJson.String(source["url"])
            ?? throw new AnthropicChatRequestException($"{path}.source.url: Field required");
        var documentUrlPart = new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject { ["url"] = url }
        };
        ApplyPartCacheControl(documentUrlPart, block, options);
        return documentUrlPart;
    }

    private static JsonObject RequireBlock(JsonNode? node, int messageIndex, int blockIndex, int nestedIndex = -1)
    {
        if (node is JsonObject block)
        {
            return block;
        }

        string path = nestedIndex >= 0
            ? $"messages.{messageIndex}.content.{blockIndex}.content.{nestedIndex}"
            : $"messages.{messageIndex}.content.{blockIndex}";
        throw new AnthropicChatRequestException($"{path}: Input should be a valid dictionary");
    }

    private static string RequireBlockType(JsonObject block, int messageIndex, int blockIndex, int nestedIndex = -1)
    {
        string? type = AnthropicChatJson.String(block["type"]);
        if (string.IsNullOrEmpty(type))
        {
            string path = nestedIndex >= 0
                ? $"messages.{messageIndex}.content.{blockIndex}.content.{nestedIndex}.type"
                : $"messages.{messageIndex}.content.{blockIndex}.type";
            throw new AnthropicChatRequestException($"{path}: Field required");
        }

        return type;
    }

    private static string ContentToText(JsonNode? content, int messageIndex)
    {
        if (AnthropicChatJson.String(content) is { } text)
        {
            return AnthropicChatJson.IsWhiteSpaceOnly(text) ? string.Empty : text;
        }

        if (content is not JsonArray blocks)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        for (int j = 0; j < blocks.Count; j++)
        {
            JsonObject block = RequireBlock(blocks[j], messageIndex, j);
            string type = RequireBlockType(block, messageIndex, j);
            if (string.Equals(type, "text", StringComparison.Ordinal))
            {
                if (AnthropicChatJson.String(block["text"]) is { } blockText &&
                    !AnthropicChatJson.IsWhiteSpaceOnly(blockText))
                {
                    lines.Add(blockText);
                }
            }
            else
            {
                lines.Add(AnthropicChatJson.Placeholder(type));
            }
        }

        return string.Join('\n', lines);
    }

    // ---------------------------------------------------------------- system

    private string? BuildSystemText(JsonNode? systemNode, AnthropicChatBridgeOptions options)
    {
        if (systemNode is null)
        {
            return null;
        }

        if (AnthropicChatJson.String(systemNode) is { } text)
        {
            return AnthropicChatJson.IsWhiteSpaceOnly(text) ? null : text;
        }

        if (systemNode is not JsonArray blocks)
        {
            return null;
        }

        var pieces = new List<string>();
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i == 0 && AnthropicChatJson.IsAttributionBlock(blocks[i]))
            {
                continue;
            }

            if (blocks[i] is JsonObject block)
            {
                if (string.Equals(AnthropicChatJson.String(block["type"]), "text", StringComparison.Ordinal))
                {
                    if (AnthropicChatJson.String(block["text"]) is { } blockText &&
                        !AnthropicChatJson.IsWhiteSpaceOnly(blockText))
                    {
                        pieces.Add(blockText);
                    }
                }
                else
                {
                    string type = AnthropicChatJson.BlockType(block);
                    logger.LogDebug("Replaced unsupported system block '{Type}' at index {Index}", type, i);
                    pieces.Add(AnthropicChatJson.Placeholder(type));
                }
            }
        }

        return pieces.Count == 0 ? null : string.Join('\n', pieces);
    }

    private static JsonObject SystemMessage(string text, JsonNode? originalSystem, AnthropicChatBridgeOptions options)
    {
        var message = new JsonObject { ["role"] = "system", ["content"] = text };

        if (options.ForwardCacheControl && originalSystem is JsonArray blocks)
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                // The first block can be the dropped attribution block; its cache_control must not survive.
                if (i == 0 && AnthropicChatJson.IsAttributionBlock(blocks[i]))
                {
                    continue;
                }

                if (blocks[i] is JsonObject block && block["cache_control"] is JsonObject cacheControl)
                {
                    message["cache_control"] = cacheControl.DeepClone();
                    break;
                }
            }
        }

        return message;
    }

    // ---------------------------------------------------------------- body assembly

    private JsonObject AssembleBody(
        JsonObject root,
        string model,
        int maxTokens,
        bool stream,
        string[]? stop,
        ToolCatalog tools,
        ToolChoice toolChoice,
        JsonArray messages,
        AnthropicChatBridgeOptions options)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages
        };

        AddNumber(root, body, "temperature");
        AddNumber(root, body, "top_p");

        int effectiveMaxTokens = Math.Max(maxTokens, MinOutputTokens);
        if (options.MaxOutputTokens is int cap && effectiveMaxTokens > cap)
        {
            effectiveMaxTokens = cap;
        }

        body[options.MaxTokensField == AnthropicChatMaxTokensField.MaxCompletionTokens
            ? "max_completion_tokens"
            : "max_tokens"] = effectiveMaxTokens;

        if (stop is { Length: > 0 })
        {
            var stopArray = new JsonArray();
            foreach (string value in stop)
            {
                stopArray.Add((JsonNode)JsonValue.Create(value));
            }

            body["stop"] = stopArray;
        }

        if (tools.Converted.Count > 0)
        {
            body["tools"] = tools.Converted;
        }

        if (toolChoice.Output is not null)
        {
            body["tool_choice"] = toolChoice.Output;
        }

        if (toolChoice.ParallelOff)
        {
            body["parallel_tool_calls"] = false;
        }

        if (BuildResponseFormat(root["output_config"], options) is { } responseFormat)
        {
            body["response_format"] = responseFormat;
        }

        if (stream)
        {
            body["stream"] = true;
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        AddReasoningControls(body, root, options);

        if (BuildUser(root["metadata"], options) is { } user)
        {
            body["user"] = user;
        }

        return body;
    }

    private static void AddNumber(JsonObject source, JsonObject target, string name)
    {
        if (source[name] is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            target[name] = value.DeepClone();
        }
    }

    private JsonNode? BuildResponseFormat(JsonNode? outputConfig, AnthropicChatBridgeOptions options)
    {
        if (!options.SendResponseFormat || outputConfig is not JsonObject config)
        {
            return null;
        }

        if (config["format"] is not JsonObject format ||
            !string.Equals(AnthropicChatJson.String(format["type"]), "json_schema", StringComparison.Ordinal) ||
            format["schema"] is not JsonObject schema)
        {
            logger.LogDebug("Ignored output_config.format with no usable json_schema");
            return null;
        }

        string name = AnthropicChatJson.String(format["name"]) is { Length: > 0 } formatName ? formatName : "response";
        return new JsonObject
        {
            ["type"] = "json_schema",
            ["json_schema"] = new JsonObject
            {
                ["name"] = name,
                ["schema"] = AddAdditionalProperties(schema)
            }
        };
    }

    private static JsonNode AddAdditionalProperties(JsonNode node)
    {
        if (node is JsonArray array)
        {
            var copy = new JsonArray();
            foreach (JsonNode? item in array)
            {
                copy.Add(AddAdditionalProperties(item ?? new JsonObject()));
            }

            return copy;
        }

        if (node is not JsonObject obj)
        {
            return node.DeepClone();
        }

        bool isCombinator = obj.ContainsKey("anyOf") || obj.ContainsKey("allOf") || obj.ContainsKey("oneOf");
        var result = new JsonObject();

        foreach (KeyValuePair<string, JsonNode?> property in obj)
        {
            result[property.Key] = property.Key switch
            {
                "properties" when property.Value is JsonObject properties => AddAdditionalPropertiesToValues(properties),
                "items" or "not" or "additionalProperties" => property.Value is null ? null : AddAdditionalProperties(property.Value),
                "anyOf" or "allOf" or "oneOf" => AddAdditionalProperties(property.Value ?? new JsonArray()),
                _ => property.Value?.DeepClone()
            };
        }

        if (!isCombinator &&
            string.Equals(AnthropicChatJson.String(obj["type"]), "object", StringComparison.Ordinal) &&
            !obj.ContainsKey("additionalProperties"))
        {
            result["additionalProperties"] = false;
        }

        return result;
    }

    private static JsonObject AddAdditionalPropertiesToValues(JsonObject properties)
    {
        var result = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> property in properties)
        {
            result[property.Key] = property.Value is null ? null : AddAdditionalProperties(property.Value);
        }

        return result;
    }

    private static JsonNode? BuildUser(JsonNode? metadata, AnthropicChatBridgeOptions options)
    {
        if (options.UserIdMode == AnthropicChatUserIdMode.Off || metadata is not JsonObject metadataObject)
        {
            return null;
        }

        string? userId = AnthropicChatJson.String(metadataObject["user_id"]);
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        if (options.UserIdMode == AnthropicChatUserIdMode.Passthrough)
        {
            return JsonValue.Create(userId);
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(userId));
        return JsonValue.Create(Convert.ToHexString(hash)[..32].ToLowerInvariant());
    }

    // ---------------------------------------------------------------- reasoning

    private static void AddReasoningControls(JsonObject body, JsonObject root, AnthropicChatBridgeOptions options)
    {
        if (options.CompatProfile == AnthropicChatCompatProfile.MiniMax)
        {
            body["reasoning_split"] = true;
        }

        if (!options.SendReasoningEffort)
        {
            return;
        }

        string? effort = CanonicalEffort(root);
        ThinkingState thinking = ThinkingStateOf(root, effort);
        string? reasoningEffort = null;

        switch (options.CompatProfile)
        {
            case AnthropicChatCompatProfile.None:
                if (thinking == ThinkingState.Enabled && effort is not null)
                {
                    reasoningEffort = effort is "xhigh" or "max" ? "high" : effort;
                }
                break;

            case AnthropicChatCompatProfile.DeepSeek:
                if (thinking == ThinkingState.Disabled)
                {
                    body["thinking"] = DisabledThinking();
                }
                else if (thinking == ThinkingState.Enabled)
                {
                    body["thinking"] = EnabledThinking();
                    if (effort is not null)
                    {
                        reasoningEffort = effort is "low" or "medium" or "high" ? "high" : "max";
                    }
                }
                break;

            case AnthropicChatCompatProfile.Mimo:
                if (thinking == ThinkingState.Disabled)
                {
                    body["thinking"] = DisabledThinking();
                }
                else if (thinking == ThinkingState.Enabled)
                {
                    body["thinking"] = EnabledThinking();
                }
                break;

            case AnthropicChatCompatProfile.Zhipu:
                if (thinking == ThinkingState.Disabled)
                {
                    body["thinking"] = DisabledThinking();
                }
                else if (thinking == ThinkingState.Enabled)
                {
                    body["thinking"] = EnabledThinking();
                    if (effort is not null)
                    {
                        reasoningEffort = effort;
                    }
                }
                break;

            case AnthropicChatCompatProfile.MiniMax:
                // No thinking key and no effort; reasoning_split is emitted above regardless of the gate.
                break;
        }

        if (reasoningEffort is not null)
        {
            body["reasoning_effort"] = reasoningEffort;
        }
    }

    private static JsonObject DisabledThinking() => new() { ["type"] = "disabled" };

    private static JsonObject EnabledThinking() => new() { ["type"] = "enabled" };

    private enum ThinkingState
    {
        Off,
        Enabled,
        Disabled
    }

    private static string? CanonicalEffort(JsonObject root)
    {
        string? explicitEffort = root["output_config"] is JsonObject outputConfig
            ? AnthropicChatJson.String(outputConfig["effort"])
            : null;

        if (explicitEffort is not null)
        {
            string lowered = explicitEffort.ToLowerInvariant();
            if (lowered is "low" or "medium" or "high" or "xhigh" or "max")
            {
                return lowered;
            }
        }

        if (root["thinking"] is JsonObject thinking &&
            string.Equals(AnthropicChatJson.String(thinking["type"]), "enabled", StringComparison.Ordinal) &&
            AnthropicChatJson.Number(thinking["budget_tokens"]) is double budget)
        {
            return budget < 8192 ? "low" : budget < 24576 ? "medium" : "high";
        }

        return null;
    }

    private static ThinkingState ThinkingStateOf(JsonObject root, string? effort)
    {
        if (root["thinking"] is JsonObject thinking)
        {
            string? type = AnthropicChatJson.String(thinking["type"]);
            if (string.Equals(type, "disabled", StringComparison.Ordinal))
            {
                return ThinkingState.Disabled;
            }

            if (string.Equals(type, "enabled", StringComparison.Ordinal) || effort is not null)
            {
                return ThinkingState.Enabled;
            }
        }
        else if (effort is not null)
        {
            return ThinkingState.Enabled;
        }

        return ThinkingState.Off;
    }
}
