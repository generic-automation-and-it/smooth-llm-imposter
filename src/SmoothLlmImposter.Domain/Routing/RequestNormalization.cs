namespace SmoothLlmImposter.Domain.Routing;

/// <summary>
/// Selects a proxy-side request-normalization profile for a provider. Normalization reshapes the
/// inbound request body so a strict OpenAI-compatible upstream accepts it (HLD 004). It is
/// <b>request-only</b> (never rewrites the response) and <b>off by default</b> — a provider that does
/// not opt in forwards byte-identically (HLD 004 LADR-02/LADR-03).
/// </summary>
public enum RequestNormalization
{
    /// <summary>No normalization — the request is forwarded unchanged (default, safe).</summary>
    None = 0,

    /// <summary>
    /// Normalize a Codex / OpenAI-SDK request for a strict OpenAI-compatible upstream: keep only
    /// upstream-valid <c>function</c> tools (drop unsupported tool <c>type</c>s, flatten
    /// <c>namespace</c> wrappers, drop names that fail the upstream charset rule) and clean any
    /// <c>tool_choice</c> that references a removed tool.
    /// </summary>
    CodexToOpenAiSdk = 1
}

/// <summary>
/// Parses the operator-facing <c>RequestNormalization</c> config string into <see cref="RequestNormalization"/>.
/// Empty/whitespace/null defaults to <see cref="RequestNormalization.None"/> (safe default).
/// </summary>
public static class RequestNormalizationParser
{
    public static bool TryParse(string? value, out RequestNormalization normalization)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            normalization = RequestNormalization.None;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "none":
                normalization = RequestNormalization.None;
                return true;
            case "codex":
            case "codex_to_openai_sdk":
            case "codex-to-openai-sdk":
            case "codextoopenaisdk":
                normalization = RequestNormalization.CodexToOpenAiSdk;
                return true;
            default:
                normalization = RequestNormalization.None;
                return false;
        }
    }

    public static RequestNormalization Parse(string? value) =>
        TryParse(value, out RequestNormalization normalization)
            ? normalization
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown request normalization.");
}
