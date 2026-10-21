using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Domain.GithubCopilot;

/// <summary>
/// A feature-owned provider identity recognized by an exact terminal key suffix. The reserved keys are
/// <c>github-anthropic</c>/<c>github-openai</c> (unprefixed pair) and <c>&lt;prefix&gt;-github-anthropic</c>/
/// <c>&lt;prefix&gt;-github-openai</c> (named pair). <see cref="Prefix"/> is the empty string for the
/// unprefixed pair. Identity is the stable provider key, never the display name (R1/D1).
/// </summary>
public sealed record GithubCopilotProfile(string Prefix, ApiDialect Dialect)
{
    public string AnthropicKey => GithubCopilotProfileKey.Key(Prefix, ApiDialect.Anthropic);

    public string OpenAiKey => GithubCopilotProfileKey.Key(Prefix, ApiDialect.OpenAi);

    public string Key => Dialect == ApiDialect.Anthropic ? AnthropicKey : OpenAiKey;
}

/// <summary>
/// Exact-suffix recognition of the reserved Copilot provider keys. Matching is terminal and case-sensitive:
/// a substring match (e.g. <c>my-github-openai-proxy</c>) is an ordinary provider, and only canonical
/// lowercase keys parse, so a case-only variant cannot reach a reserved identity by accident.
/// </summary>
public static class GithubCopilotProfileKey
{
    private const string AnthropicSuffix = "-github-anthropic";
    private const string OpenAiSuffix = "-github-openai";
    private const string AnthropicBare = "github-anthropic";
    private const string OpenAiBare = "github-openai";

    /// <summary>Longest accepted prefix. The full key stays well inside the admin key limit (128).</summary>
    public const int MaxPrefixLength = 48;

    public static string Key(string prefix, ApiDialect dialect) =>
        string.IsNullOrEmpty(prefix)
            ? dialect == ApiDialect.Anthropic ? AnthropicBare : OpenAiBare
            : prefix + (dialect == ApiDialect.Anthropic ? AnthropicSuffix : OpenAiSuffix);

    /// <summary>
    /// Recognizes a reserved key. <paramref name="profile"/> is null and the result is <c>false</c> for any
    /// ordinary provider key, including a reserved word used as a substring rather than a terminal suffix.
    /// </summary>
    public static bool TryParse(string key, out GithubCopilotProfile? profile)
    {
        if (key.Equals(AnthropicBare, StringComparison.Ordinal))
        {
            profile = new GithubCopilotProfile(string.Empty, ApiDialect.Anthropic);
            return true;
        }

        if (key.Equals(OpenAiBare, StringComparison.Ordinal))
        {
            profile = new GithubCopilotProfile(string.Empty, ApiDialect.OpenAi);
            return true;
        }

        if (TryParsePrefixed(key, AnthropicSuffix, ApiDialect.Anthropic, out profile))
        {
            return true;
        }

        return TryParsePrefixed(key, OpenAiSuffix, ApiDialect.OpenAi, out profile);
    }

    public static bool IsReservedKey(string key) => TryParse(key, out _);

    /// <summary>
    /// Validates an operator-supplied prefix slug. Null or empty is accepted and means the unprefixed pair;
    /// a supplied prefix must be canonical (already lowercase) and may contain only <c>a-z</c>, <c>0-9</c>
    /// and single hyphens, without leading/trailing hyphens.
    /// </summary>
    public static bool TryNormalizePrefix(string? prefix, out string normalized)
    {
        normalized = string.Empty;
        if (prefix is null)
        {
            return true;
        }

        if (prefix.Length == 0)
        {
            return true;
        }

        if (prefix.Length > MaxPrefixLength ||
            prefix[0] == '-' ||
            prefix[^1] == '-' ||
            prefix.Contains("--", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char c in prefix)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
            {
                return false;
            }
        }

        normalized = prefix;
        return true;
    }

    private static bool TryParsePrefixed(string key, string suffix, ApiDialect dialect, out GithubCopilotProfile? profile)
    {
        profile = null;
        if (!key.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        string prefix = key[..^suffix.Length];
        if (!TryNormalizePrefix(prefix, out string normalized) || string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        profile = new GithubCopilotProfile(normalized, dialect);
        return true;
    }
}
