namespace SmoothLlmImposter.Domain.Routing;

/// <summary>
/// The wire dialect a request and its upstream speak. The imposter routes within a single
/// dialect only — it never translates an OpenAI request into an Anthropic one or vice versa.
/// </summary>
public enum ApiDialect
{
    OpenAi,
    Anthropic
}

/// <summary>Parses the configured <c>Api</c> string ("openai" / "anthropic") into <see cref="ApiDialect"/>.</summary>
public static class ApiDialectParser
{
    public static bool TryParse(string? value, out ApiDialect dialect)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "openai":
                dialect = ApiDialect.OpenAi;
                return true;
            case "anthropic":
                dialect = ApiDialect.Anthropic;
                return true;
            default:
                dialect = default;
                return false;
        }
    }

    public static ApiDialect Parse(string? value) =>
        TryParse(value, out ApiDialect dialect)
            ? dialect
            : throw new ArgumentException($"Unknown API dialect '{value}'. Expected 'openai' or 'anthropic'.", nameof(value));
}
