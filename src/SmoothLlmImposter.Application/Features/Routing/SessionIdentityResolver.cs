using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Resolves a per-request session identity from inbound headers, then body fields, then a stable
/// fingerprint of caller identity material. Pure and stateless — never persists or logs raw values
/// (HLD 009 / NFR-001).
/// </summary>
internal static class SessionIdentityResolver
{
    // First match wins, case-insensitive. Order prefers explicit session markers over generic ones.
    private static readonly string[] HeaderCandidates =
    [
        "session_id",
        "x-opencode-session",
        "x-session-id",
        "conversation_id"
    ];

    // Stable, non-ephemeral caller identity headers used only as fingerprint inputs when nothing was
    // captured. Values are hashed, never logged. Each entry maps to a LADR-03 fingerprint bullet; the
    // sixth input (body `user`) comes from the same single body parse, added by Resolve.
    private static readonly string[] FingerprintHeaderNames =
    [
        "chatgpt-account-id",   // LADR-03: Codex subscription identity
        "openai-organization",  // LADR-03: workspace pin
        "openai-project",       // LADR-03: workspace pin
        "authorization",        // LADR-03: most stable CLI credential
        "x-api-key"             // LADR-03: most stable CLI credential
    ];

    public static SessionIdentity Resolve(CallerHeaders callerHeaders, string? requestBody)
    {
        foreach (string headerName in HeaderCandidates)
        {
            if (FirstNonBlank(callerHeaders.Get(headerName)) is { } captured)
            {
                return new SessionIdentity(captured, SessionIdentitySource.Captured);
            }
        }

        // Parse the body once: capture fields and the fingerprint's body.user share a single read.
        BodySignals body = ParseBody(requestBody);

        if (body.PromptCacheKey is not null)
        {
            return new SessionIdentity(body.PromptCacheKey, SessionIdentitySource.Captured);
        }

        if (body.MetadataUserId is not null)
        {
            return new SessionIdentity(body.MetadataUserId, SessionIdentitySource.Captured);
        }

        if (TryDeriveFingerprint(callerHeaders, body.User, out string derived))
        {
            return new SessionIdentity(derived, SessionIdentitySource.Derived);
        }

        return SessionIdentity.None;
    }

    /// <summary>Body fields the resolver reads, extracted from a single parse (null when absent/blank).</summary>
    private readonly record struct BodySignals(string? PromptCacheKey, string? MetadataUserId, string? User);

    private static BodySignals ParseBody(string? requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return default;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(requestBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            string? metadataUserId = null;
            if (document.RootElement.TryGetProperty("metadata", out JsonElement metadata) &&
                metadata.ValueKind == JsonValueKind.Object &&
                TryReadString(metadata, "user_id", out string userId))
            {
                metadataUserId = userId;
            }

            return new BodySignals(
                TryReadString(document.RootElement, "prompt_cache_key", out string cacheKey) ? cacheKey : null,
                metadataUserId,
                TryReadString(document.RootElement, "user", out string user) ? user : null);
        }
        catch (JsonException)
        {
            // Body capture is best-effort; invalid JSON is rejected later by the router/transformer.
            return default;
        }
    }

    private static bool TryDeriveFingerprint(CallerHeaders callerHeaders, string? bodyUser, out string derived)
    {
        derived = string.Empty;
        var parts = new List<string>(FingerprintHeaderNames.Length + 1);

        foreach (string headerName in FingerprintHeaderNames)
        {
            if (FirstNonBlank(callerHeaders.Get(headerName)) is { } value)
            {
                // Canonicalize header name so fingerprint is casing-stable across clients.
                parts.Add(headerName.ToLowerInvariant() + "=" + value);
            }
        }

        if (bodyUser is not null)
        {
            parts.Add("body.user=" + bodyUser);
        }

        if (parts.Count == 0)
        {
            return false;
        }

        // Sort so header enumeration order cannot fork the fingerprint.
        parts.Sort(StringComparer.Ordinal);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts)));
        // Compact, URL-safe-ish id; prefix marks it as derived so diag is distinguishable from captured.
        derived = "derived-" + Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
        return true;
    }

    private static bool TryReadString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? raw = element.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        value = raw.Trim();
        return true;
    }

    private static string? FirstNonBlank(IReadOnlyList<string>? values)
    {
        if (values is null)
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
