using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.Routing.Decisions;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

internal sealed class ToolCatalogOrderer(IOptions<ImposterOptions> options, TimeProvider timeProvider) : IToolCatalogOrderer
{
    internal const int MaxTools = 1024;

    private readonly SessionScopedMemory<Dictionary<string, int>> _sessions =
        new(timeProvider, () => new Dictionary<string, int>(StringComparer.Ordinal));

    public string Order(ApiDialect dialect, string path, string body, CallerHeaders callerHeaders)
    {
        if (!options.Value.ToolOrdering.Enabled || string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        DecisionBodyShape shape = DecisionJson.ShapeFor(path);
        if (shape == DecisionBodyShape.Unknown ||
            (dialect == ApiDialect.Anthropic) != (shape == DecisionBodyShape.Anthropic))
        {
            return body;
        }

        JsonObject root = JsonNodeMaterializer.ParseObject(body);
        if (root["tools"] is not JsonArray { Count: > 0 } tools || tools.Count > MaxTools ||
            AnthropicRequestTransformer.HasCallerCacheControl(root) ||
            root["prompt_cache_options"] is not null ||
            HasCallerPromptCacheBreakpoint(root) ||
            DecisionTool.UsesDeferredLoading(tools) ||
            (shape == DecisionBodyShape.Responses &&
             (root["previous_response_id"] is not null || root["conversation"] is not null)))
        {
            return body;
        }

        SessionIdentity identity = SessionIdentityResolver.Resolve(callerHeaders, body);
        if (identity.Source != SessionIdentitySource.Captured || !identity.HasValue)
        {
            return body;
        }

        string[] keys = tools.Select(tool => ToolKey(tool, shape)).ToArray();
        if (keys.Any(string.IsNullOrEmpty) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
        {
            return body;
        }

        string scope = SessionScope.Key(dialect.ToString(), DecisionJson.Text(root["model"]), identity.Value!, callerHeaders);
        int[]? order = _sessions.Use(scope, ranks =>
        {
            if (ranks.Count + keys.Count(key => !ranks.ContainsKey(key)) > MaxTools)
            {
                return null;
            }

            foreach (string key in keys)
            {
                ranks.TryAdd(key, ranks.Count);
            }
            return Enumerable.Range(0, keys.Length).OrderBy(index => ranks[keys[index]]).ToArray();
        });

        if (order is null)
        {
            return body;
        }

        if (order.SequenceEqual(Enumerable.Range(0, keys.Length)))
        {
            return body;
        }

        var ordered = new JsonArray();
        foreach (int index in order)
        {
            ordered.Add(tools[index]!.DeepClone());
        }
        root["tools"] = ordered;
        return DecisionJson.ReplaceTopLevel(body, root, "tools");
    }

    private static bool HasCallerPromptCacheBreakpoint(JsonObject root) =>
        HasPromptCacheBreakpoint(root["tools"] as JsonArray) ||
        (root["input"] is JsonArray input &&
         input.OfType<JsonObject>().Any(item => HasPromptCacheBreakpoint(item["content"] as JsonArray)));

    private static bool HasPromptCacheBreakpoint(JsonArray? blocks) =>
        blocks?.OfType<JsonObject>().Any(block => block["prompt_cache_breakpoint"] is not null) == true;

    private static string ToolKey(JsonNode? node, DecisionBodyShape shape)
    {
        if (node is not JsonObject tool)
        {
            return string.Empty;
        }

        string type = DecisionJson.Text(tool["type"]);
        string name = shape switch
        {
            DecisionBodyShape.Anthropic when type.Length == 0 && tool["input_schema"] is JsonObject => DecisionJson.Text(tool["name"]),
            DecisionBodyShape.Responses when type is "function" or "custom" => DecisionJson.Text(tool["name"]),
            DecisionBodyShape.Chat when type is "function" or "custom" => DecisionJson.Text((tool[type] as JsonObject)?["name"]),
            _ => string.Empty
        };
        return string.IsNullOrWhiteSpace(name) || name.Length > 512
            ? string.Empty
            : SessionScope.Hash(new JsonArray(type, name).ToJsonString());
    }
}
