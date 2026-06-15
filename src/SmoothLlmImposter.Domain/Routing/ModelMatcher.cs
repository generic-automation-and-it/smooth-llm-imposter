namespace SmoothLlmImposter.Domain.Routing;

/// <summary>
/// Matches an inbound model name against a mapping's <c>From</c> pattern. Supports exact match and a
/// single trailing-<c>*</c> wildcard (e.g. <c>claude-haiku-*</c> matches any suffix). Case-insensitive.
/// </summary>
public static class ModelMatcher
{
    public static bool Matches(string pattern, string model)
    {
        if (string.IsNullOrEmpty(pattern) || model is null)
        {
            return false;
        }

        if (pattern.EndsWith('*'))
        {
            return model.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(pattern, model, StringComparison.OrdinalIgnoreCase);
    }
}
