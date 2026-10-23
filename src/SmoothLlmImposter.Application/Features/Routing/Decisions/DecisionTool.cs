using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

internal sealed record DecisionTool(int Index, string Label, string Description)
{
    public string Key => $"t{Index}";

    public static DecisionTool[] ReadCandidates(JsonArray tools, JsonObject root, DecisionBodyShape shape) =>
        tools.Select((node, index) => (node, index))
            .Where(item => item.node is JsonObject tool && IsKnown(tool, shape) && !IsReferenced(tool, root, shape))
            .Select(item =>
            {
                var tool = (JsonObject)item.node!;
                return new DecisionTool(item.index, LabelFor(tool), DescriptionFor(tool));
            }).ToArray();

    public static bool UsesDeferredLoading(JsonArray tools) =>
        tools.OfType<JsonObject>().Any(tool =>
            DecisionJson.Text(tool["type"]).StartsWith("tool_search", StringComparison.Ordinal) ||
            IsDeferred(tool) ||
            DecisionJson.Objects(tool["tools"]).Any(IsDeferred));

    private static bool IsDeferred(JsonObject tool) =>
        tool["defer_loading"] is JsonValue value && value.TryGetValue(out bool deferred) && deferred;

    public static void PruneChoice(JsonObject root, JsonArray tools, HashSet<int> removed, DecisionBodyShape shape)
    {
        if (root["tool_choice"] is not JsonObject choice)
        {
            return;
        }

        JsonObject[] survivors = tools.Where((_, index) => !removed.Contains(index)).OfType<JsonObject>().ToArray();
        string type = DecisionJson.Text(choice["type"]);
        if (type == "allowed_tools" && shape != DecisionBodyShape.Anthropic)
        {
            JsonObject? container = shape == DecisionBodyShape.Chat ? choice["allowed_tools"] as JsonObject : choice;
            if (container?["tools"] is not JsonArray allowed)
            {
                return;
            }

            for (int index = allowed.Count - 1; index >= 0; index--)
            {
                if (allowed[index] is JsonObject entry && IsKnown(entry, shape) &&
                    !survivors.Any(tool => MatchesChoice(tool, entry, shape)))
                {
                    allowed.RemoveAt(index);
                }
            }

            if (allowed.Count == 0)
            {
                root.Remove("tool_choice");
            }
        }
        else if (IsKnownChoice(choice, shape) && !survivors.Any(tool => MatchesChoice(tool, choice, shape)))
        {
            root.Remove("tool_choice");
        }
    }

    private static bool IsKnown(JsonObject tool, DecisionBodyShape shape)
    {
        string type = DecisionJson.Text(tool["type"]);
        return shape switch
        {
            DecisionBodyShape.Chat => type switch
            {
                "function" => HasName(tool["function"] as JsonObject),
                "custom" => HasName(tool["custom"] as JsonObject),
                _ => false
            },
            DecisionBodyShape.Responses => type switch
            {
                "function" or "custom" => HasName(tool),
                "mcp" => !string.IsNullOrWhiteSpace(DecisionJson.Text(tool["server_label"])),
                "namespace" => HasName(tool) && tool["tools"] is JsonArray,
                "web_search" or "web_search_preview" or "image_generation" or "local_shell" or "file_search" => true,
                _ => false
            },
            DecisionBodyShape.Anthropic => HasName(tool) &&
                ((type.Length == 0 && tool["input_schema"] is JsonObject) ||
                 type.StartsWith("web_search_", StringComparison.Ordinal)),
            _ => false
        };
    }

    private static bool HasName(JsonObject? tool) =>
        tool is not null && !string.IsNullOrWhiteSpace(DecisionJson.Text(tool["name"]));

    private static string LabelFor(JsonObject tool)
    {
        string label = NameFor(tool);
        if (label.Length == 0)
        {
            label = DecisionJson.Text(tool["server_label"]);
        }

        if (label.Length == 0)
        {
            label = DecisionJson.Text(tool["type"]);
        }

        if (DecisionJson.Text(tool["type"]) == "namespace")
        {
            label += " " + string.Join(" ", DecisionJson.Objects(tool["tools"]).Select(NameFor));
        }

        return label;
    }

    private static string NameFor(JsonObject tool)
    {
        string name = DecisionJson.Text(tool["name"]);
        if (name.Length == 0 && tool["function"] is JsonObject function)
        {
            name = DecisionJson.Text(function["name"]);
        }

        if (name.Length == 0 && tool["custom"] is JsonObject custom)
        {
            name = DecisionJson.Text(custom["name"]);
        }

        return name;
    }

    private static string DescriptionFor(JsonObject tool)
    {
        string description = DecisionJson.Text(tool["description"]);
        if (description.Length == 0 && tool["function"] is JsonObject function)
        {
            description = DecisionJson.Text(function["description"]);
        }

        if (description.Length == 0 && tool["custom"] is JsonObject custom)
        {
            description = DecisionJson.Text(custom["description"]);
        }

        return description;
    }

    private static bool IsReferenced(JsonObject tool, JsonObject root, DecisionBodyShape shape) => shape switch
    {
        DecisionBodyShape.Chat => DecisionJson.Objects(root["messages"])
            .Where(message => DecisionJson.Text(message["role"]) == "assistant")
            .SelectMany(message => DecisionJson.Objects(message["tool_calls"]))
            .Any(call => NameFor(call) == NameFor(tool)),
        DecisionBodyShape.Responses => DecisionJson.Objects(root["input"])
            .Any(item => ReferencesResponseTool(tool, item)),
        DecisionBodyShape.Anthropic => DecisionJson.Objects(root["messages"])
            .SelectMany(message => DecisionJson.Objects(message["content"]))
            .Where(block => DecisionJson.Text(block["type"]) is "tool_use" or "server_tool_use" or "mcp_tool_use")
            .Any(block => NameFor(block) == NameFor(tool)),
        _ => false
    };

    private static bool ReferencesResponseTool(JsonObject tool, JsonObject item)
    {
        string type = DecisionJson.Text(item["type"]);
        string toolType = DecisionJson.Text(tool["type"]);
        if (type is "function_call" or "custom_tool_call" or "mcp_call")
        {
            string name = NameFor(item);
            if (name.Length > 0 && name == NameFor(tool))
            {
                return true;
            }

            if (toolType == "namespace" &&
                (DecisionJson.Text(item["namespace"]) == NameFor(tool) ||
                 DecisionJson.Objects(tool["tools"]).Any(child => name.Length > 0 && NameFor(child) == name)))
            {
                return true;
            }

            if (type == "mcp_call" && toolType == "mcp" &&
                DecisionJson.Text(item["server_label"]) == DecisionJson.Text(tool["server_label"]))
            {
                return true;
            }
        }

        return type.EndsWith("_call", StringComparison.Ordinal) &&
            type is not ("function_call" or "custom_tool_call" or "mcp_call") &&
            toolType.StartsWith(type[..^5], StringComparison.Ordinal);
    }

    private static bool IsKnownChoice(JsonObject choice, DecisionBodyShape shape) =>
        shape == DecisionBodyShape.Anthropic
            ? DecisionJson.Text(choice["type"]) == "tool" && HasName(choice)
            : DecisionJson.Text(choice["type"]) is "function" or "custom" && IsKnown(choice, shape);

    private static bool MatchesChoice(JsonObject tool, JsonObject choice, DecisionBodyShape shape)
    {
        if (shape == DecisionBodyShape.Anthropic)
        {
            return NameFor(tool) == NameFor(choice);
        }

        if (shape == DecisionBodyShape.Responses && DecisionJson.Text(tool["type"]) == "namespace")
        {
            string requestedNamespace = DecisionJson.Text(choice["namespace"]);
            return (requestedNamespace.Length == 0 || requestedNamespace == NameFor(tool)) &&
                DecisionJson.Objects(tool["tools"]).Any(child =>
                    DecisionJson.Text(child["type"]) == DecisionJson.Text(choice["type"]) &&
                    NameFor(child) == NameFor(choice));
        }

        string choiceNamespace = DecisionJson.Text(choice["namespace"]);
        if (choiceNamespace.Length > 0 && choiceNamespace != DecisionJson.Text(tool["namespace"]))
        {
            return false;
        }

        if (DecisionJson.Text(tool["type"]) != DecisionJson.Text(choice["type"]))
        {
            return false;
        }

        return DecisionJson.Text(tool["type"]) == "mcp"
            ? DecisionJson.Text(tool["server_label"]) == DecisionJson.Text(choice["server_label"])
            : NameFor(tool) == NameFor(choice);
    }
}
