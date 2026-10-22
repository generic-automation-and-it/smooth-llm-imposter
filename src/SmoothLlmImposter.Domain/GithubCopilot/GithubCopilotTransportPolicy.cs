using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Domain.GithubCopilot;

/// <summary>
/// Path and origin policy for a managed Copilot credential (R2/R4). Only the account-discovered API origin
/// is accepted, so an admin-supplied provider <c>BaseUrl</c> can never redirect a managed GitHub token.
/// Only the same-dialect completion paths are forwarded; Copilot Chat/Completions is out of scope, so an
/// unsupported method or path is rejected before any credential leaves the process.
/// </summary>
public static class GithubCopilotTransportPolicy
{
    public const string MessagesPath = "/v1/messages";
    public const string ResponsesPath = "/responses";

    private const string CopilotHost = "githubcopilot.com";

    // Headers a Copilot client is expected to send alongside the managed Bearer token, aligned with opencode's
    // GitHub Copilot provider (the reference client GitHub serves the full catalog to).
    public const string ApiVersionHeader = "X-GitHub-Api-Version";
    public const string ApiVersionValue = "2026-08-01";

    // Never sent: GitHub resolves the integrator from the OAuth client ID (as opencode relies on), and an
    // unrecognised value was answered as "copilot-language-server" with a narrower model list. A
    // caller-relayed value is stripped so it cannot re-select an integrator for the managed token.
    public const string IntegrationIdHeader = "Copilot-Integration-Id";

    // "agent" marks a tool-loop continuation or a non-primary request; only "user" turns are the caller's own.
    public const string InitiatorHeader = "X-Initiator";
    public const string InitiatorUser = "user";
    public const string InitiatorAgent = "agent";

    public const string OpenAiIntentHeader = "Openai-Intent";
    public const string OpenAiIntentValue = "conversation-edits";

    public const string InteractionTypeHeader = "X-Interaction-Type";
    public const string InteractionIdHeader = "X-Interaction-Id";
    public const string InteractionAgent = "conversation-agent";
    public const string InteractionSubagent = "conversation-subagent";
    public const string InteractionBackground = "conversation-background";
    public const string InteractionCompaction = "conversation-compaction";

    public const string VisionRequestHeader = "Copilot-Vision-Request";

    public const string AnthropicBetaHeader = "anthropic-beta";
    public const string InterleavedThinkingBeta = "interleaved-thinking-2025-05-14";

    public const string UserAgentValue = "SmoothLlmImposter/1.0";

    /// <summary>
    /// Maps an inbound path to the Copilot upstream path for the profile's dialect. Anthropic keeps
    /// <c>/v1/messages</c>; OpenAI accepts either <c>/responses</c> or <c>/v1/responses</c> and forwards
    /// <c>/responses</c>. Every other path — including Chat Completions — is refused.
    /// </summary>
    public static bool TryResolveUpstreamPath(ApiDialect dialect, string inboundPath, out string upstreamPath)
    {
        upstreamPath = string.Empty;

        if (dialect == ApiDialect.Anthropic)
        {
            if (!string.Equals(inboundPath, MessagesPath, StringComparison.Ordinal))
            {
                return false;
            }

            upstreamPath = MessagesPath;
            return true;
        }

        if (string.Equals(inboundPath, ResponsesPath, StringComparison.Ordinal) ||
            string.Equals(inboundPath, "/v1" + ResponsesPath, StringComparison.Ordinal))
        {
            upstreamPath = ResponsesPath;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates a persisted/discovered API origin before a managed credential is sent to it: HTTPS on the
    /// default TLS port, no userinfo/query/fragment/path, and a GitHub-controlled host — the apex
    /// <c>githubcopilot.com</c> or a subdomain of it. Lookalikes (<c>githubcopilot.com.evil.test</c>),
    /// IP literals and local/private destinations are refused.
    /// </summary>
    public static bool IsAllowedOrigin(string? origin) =>
        TryParseAllowedOrigin(origin, out _);

    public static bool TryParseAllowedOrigin(string? origin, out Uri parsed)
    {
        parsed = null!;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !(string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/") ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.Basic))
        {
            return false;
        }

        string host = uri.Host.ToLowerInvariant();
        if (host == CopilotHost)
        {
            parsed = uri;
            return true;
        }

        if (!host.EndsWith("." + CopilotHost, StringComparison.Ordinal))
        {
            return false;
        }

        // Require at least one non-empty label before the suffix, and no label may be empty — a trailing
        // dot or a doubled dot is not a GitHub-controlled host.
        string label = host[..^(CopilotHost.Length + 1)];
        if (label.Length == 0 || label.Contains("..", StringComparison.Ordinal) || label.EndsWith('.'))
        {
            return false;
        }

        parsed = uri;
        return true;
    }
}
