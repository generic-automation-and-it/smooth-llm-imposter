using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.Routing.Decisions;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

internal sealed class ToolCatalogOrderer(IOptions<ImposterOptions> options, TimeProvider timeProvider) : IToolCatalogOrderer
{
    internal const int MaxSessions = 256;
    internal const int MaxTools = 1024;
    internal static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, SessionRanks> _sessions = new(StringComparer.Ordinal);

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

        string scope = ScopeKey(dialect, DecisionJson.Text(root["model"]), identity.Value!, callerHeaders);
        int[] order;
        lock (_gate)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            RemoveExpiredSessions(now);
            if (!_sessions.TryGetValue(scope, out SessionRanks? session))
            {
                if (_sessions.Count == MaxSessions)
                {
                    _sessions.Remove(_sessions.MinBy(entry => entry.Value.LastUsed).Key);
                }
                session = new SessionRanks(now);
                _sessions.Add(scope, session);
            }

            session.LastUsed = now;
            if (session.Ranks.Count + keys.Count(key => !session.Ranks.ContainsKey(key)) > MaxTools)
            {
                return body;
            }

            foreach (string key in keys)
            {
                session.Ranks.TryAdd(key, session.Ranks.Count);
            }
            order = Enumerable.Range(0, keys.Length).OrderBy(index => session.Ranks[keys[index]]).ToArray();
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

    private void RemoveExpiredSessions(DateTimeOffset now)
    {
        string[] expired = _sessions.Where(entry => now - entry.Value.LastUsed >= IdleLifetime)
            .Select(entry => entry.Key).ToArray();
        foreach (string scope in expired)
        {
            _sessions.Remove(scope);
        }
    }

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
            : Hash(new JsonArray(type, name).ToJsonString());
    }

    private static string ScopeKey(ApiDialect dialect, string model, string identity, CallerHeaders headers)
    {
        var scope = new JsonArray(dialect.ToString(), model, identity);
        foreach (string header in new[] { "authorization", "x-api-key", "chatgpt-account-id", "openai-organization", "openai-project" })
        {
            var values = new JsonArray();
            foreach (string value in headers.Get(header) ?? [])
            {
                values.Add((JsonNode)JsonValue.Create(value));
            }
            scope.Add((JsonNode)values);
        }
        return Hash(scope.ToJsonString());
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class SessionRanks(DateTimeOffset lastUsed)
    {
        public Dictionary<string, int> Ranks { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset LastUsed { get; set; } = lastUsed;
    }
}
