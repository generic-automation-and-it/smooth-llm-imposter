using System.Text.Json;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.GithubCopilot;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Infrastructure.Routing;

/// <summary>
/// Per-request Copilot client metadata, mirroring opencode's GitHub Copilot provider: interaction type/id,
/// initiator and vision. Interaction type comes from the agent's own request-class hints (Claude Code, Codex);
/// initiator and vision are read from the forwarded body, as neither agent sends a header for them.
/// </summary>
internal readonly record struct GithubCopilotRequestHints(
    string InteractionType,
    string? InteractionId,
    bool AgentInitiated,
    bool Vision)
{
    // Session ids Claude Code (X-Claude-Code-Session-Id) and Codex (session-id; session_id before that) send,
    // then the generic markers the HLD 009 resolver also accepts. Subagents share their parent's session id.
    private static readonly string[] InteractionIdHeaders =
    [
        "X-Claude-Code-Session-Id",
        "session-id",
        "session_id",
        "x-session-id",
        "x-opencode-session",
        "conversation_id"
    ];

    public static GithubCopilotRequestHints Resolve(ApiDialect dialect, CallerHeaders headers, string? body)
    {
        string interactionType = ResolveInteractionType(headers);
        (bool bodyAgent, bool vision) = InspectBody(dialect, body);

        return new GithubCopilotRequestHints(
            interactionType,
            ResolveInteractionId(headers),
            bodyAgent || interactionType != GithubCopilotTransportPolicy.InteractionAgent,
            vision);
    }

    internal static string ResolveInteractionType(CallerHeaders headers)
    {
        // Claude Code: x-claude-code-request-class is sent only with CLAUDE_CODE_GATEWAY_HINT_HEADERS set
        // (or on first-party gates); x-claude-code-agent-id is always sent for a non-main agent.
        switch (First(headers, "x-claude-code-request-class"))
        {
            case "main":
                return GithubCopilotTransportPolicy.InteractionAgent;
            case "subagent" or "workflow":
                return GithubCopilotTransportPolicy.InteractionSubagent;
            case "compaction":
                return GithubCopilotTransportPolicy.InteractionCompaction;
            case "auxiliary" or "side_reply":
                return GithubCopilotTransportPolicy.InteractionBackground;
        }

        // Codex: x-openai-subagent is review | compact | memory_consolidation | collab_spawn | <custom label>.
        switch (First(headers, "x-openai-subagent"))
        {
            case null:
                break;
            case "compact":
                return GithubCopilotTransportPolicy.InteractionCompaction;
            case "memory_consolidation":
                return GithubCopilotTransportPolicy.InteractionBackground;
            default:
                return GithubCopilotTransportPolicy.InteractionSubagent;
        }

        return First(headers, "x-claude-code-agent-id") is not null
            ? GithubCopilotTransportPolicy.InteractionSubagent
            : GithubCopilotTransportPolicy.InteractionAgent;
    }

    private static string? ResolveInteractionId(CallerHeaders headers)
    {
        foreach (string name in InteractionIdHeaders)
        {
            if (First(headers, name) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    internal static (bool Agent, bool Vision) InspectBody(ApiDialect dialect, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (false, false);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (false, false);
            }

            return dialect == ApiDialect.Anthropic ? InspectMessages(root) : InspectResponses(root);
        }
        catch (JsonException)
        {
            return (false, false);
        }
    }

    // Anthropic Messages: a turn is agent-initiated unless the last message is a user message carrying
    // something other than tool results. Images may sit at the top level or inside a tool_result.
    private static (bool Agent, bool Vision) InspectMessages(JsonElement root)
    {
        if (!root.TryGetProperty("messages", out JsonElement messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return (false, false);
        }

        bool vision = false;
        JsonElement last = default;
        foreach (JsonElement message in messages.EnumerateArray())
        {
            last = message;
            if (!vision && TryGetArray(message, "content", out JsonElement content))
            {
                foreach (JsonElement part in content.EnumerateArray())
                {
                    string? type = TypeOf(part);
                    if (type == "image" ||
                        (type == "tool_result" && TryGetArray(part, "content", out JsonElement nested) && ContainsType(nested, "image")))
                    {
                        vision = true;
                        break;
                    }
                }
            }
        }

        return (!IsUserTurn(last), vision);

        static bool IsUserTurn(JsonElement message)
        {
            if (message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("role", out JsonElement role) ||
                role.ValueKind != JsonValueKind.String ||
                role.GetString() != "user" ||
                !message.TryGetProperty("content", out JsonElement content))
            {
                return false;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                return !string.IsNullOrWhiteSpace(content.GetString());
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement part in content.EnumerateArray())
            {
                if (TypeOf(part) != "tool_result")
                {
                    return true;
                }
            }

            return false;
        }
    }

    // OpenAI Responses: a turn is agent-initiated unless the last input item is a user message (a
    // function_call_output has no role). Images are input_image parts in a message or a tool output.
    private static (bool Agent, bool Vision) InspectResponses(JsonElement root)
    {
        if (!root.TryGetProperty("input", out JsonElement input) || input.ValueKind != JsonValueKind.Array)
        {
            return (false, false);
        }

        bool vision = false;
        JsonElement last = default;
        foreach (JsonElement item in input.EnumerateArray())
        {
            last = item;
            if (!vision &&
                ((TryGetArray(item, "content", out JsonElement content) && ContainsType(content, "input_image")) ||
                 (TryGetArray(item, "output", out JsonElement output) && ContainsType(output, "input_image"))))
            {
                vision = true;
            }
        }

        bool userTurn = last.ValueKind == JsonValueKind.Object &&
                        last.TryGetProperty("role", out JsonElement role) &&
                        role.ValueKind == JsonValueKind.String &&
                        role.GetString() == "user";

        return (!userTurn, vision);
    }

    private static bool ContainsType(JsonElement parts, string type)
    {
        foreach (JsonElement part in parts.EnumerateArray())
        {
            if (TypeOf(part) == type)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetArray(JsonElement element, string name, out JsonElement array)
    {
        array = default;
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(name, out array) &&
               array.ValueKind == JsonValueKind.Array;
    }

    private static string? TypeOf(JsonElement part) =>
        part.ValueKind == JsonValueKind.Object &&
        part.TryGetProperty("type", out JsonElement type) &&
        type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

    private static string? First(CallerHeaders headers, string name)
    {
        if (headers.Get(name) is not { } values)
        {
            return null;
        }

        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }
}
