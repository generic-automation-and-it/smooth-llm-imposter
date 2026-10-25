using System.Text.RegularExpressions;

namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// Deterministic validation of an optimizer rewrite against its original (HLD 017 LADR-04, NFR-02).
/// The original wins on doubt: any failed check rejects the rewrite.
/// </summary>
public static class MessageRewriteValidator
{
    public sealed record Result(bool IsValid, IReadOnlyList<string> FailedElements)
    {
        public static readonly Result Valid = new(true, Array.Empty<string>());
    }

    private static readonly Regex CodeSpan = new(@"`[^`]+`", RegexOptions.Compiled);
    private static readonly Regex Url = new(@"https?://[^\s)>\]]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Number = new(@"\b\d+(?:\.\d+){0,3}\b", RegexOptions.Compiled);
    private static readonly Regex EnvVar = new(@"\b[A-Z][A-Z0-9_]{2,}\b", RegexOptions.Compiled);
    private static readonly Regex SlashCmd = new(@"/[A-Za-z][A-Za-z0-9_-]*", RegexOptions.Compiled);
    private static readonly Regex DollarCmd = new(@"\$[A-Za-z][A-Za-z0-9_-]*", RegexOptions.Compiled);
    private static readonly Regex Mention = new(@"@[A-Za-z0-9_.-]+", RegexOptions.Compiled);

    public static Result Validate(string original, string rewrite, IReadOnlyCollection<string>? protectedNames = null)
    {
        var failed = new List<string>();

        if (string.IsNullOrEmpty(rewrite) || rewrite.Length >= original.Length)
        {
            failed.Add("length");
        }

        // Protected elements from original must all appear in rewrite.
        foreach (Match m in CodeSpan.Matches(original))
        {
            if (!rewrite.Contains(m.Value, StringComparison.Ordinal)) failed.Add($"code:{m.Value}");
        }
        foreach (Match m in Url.Matches(original))
        {
            if (!rewrite.Contains(m.Value, StringComparison.Ordinal)) failed.Add($"url:{m.Value}");
        }
        foreach (Match m in Number.Matches(original))
        {
            if (!rewrite.Contains(m.Value, StringComparison.Ordinal)) failed.Add($"number:{m.Value}");
        }
        foreach (Match m in EnvVar.Matches(original))
        {
            if (!rewrite.Contains(m.Value, StringComparison.Ordinal)) failed.Add($"env:{m.Value}");
        }
        foreach (Match m in SlashCmd.Matches(original))
        {
            if (!rewrite.Contains(m.Value, StringComparison.Ordinal)) failed.Add($"slash:{m.Value}");
        }
        foreach (Match m in DollarCmd.Matches(original))
        {
            if (!rewrite.Contains(m.Value, StringComparison.Ordinal)) failed.Add($"dollar:{m.Value}");
        }
        foreach (Match m in Mention.Matches(original))
        {
            if (!rewrite.Contains(m.Value, StringComparison.Ordinal)) failed.Add($"mention:{m.Value}");
        }
        if (protectedNames is not null)
        {
            foreach (string name in protectedNames)
            {
                if (!string.IsNullOrWhiteSpace(name) && !rewrite.Contains(name, StringComparison.Ordinal))
                {
                    failed.Add($"name:{name}");
                }
            }
        }

        // Introduced elements: every code span / number / env in rewrite must exist in original.
        foreach (Match m in CodeSpan.Matches(rewrite))
        {
            if (!original.Contains(m.Value, StringComparison.Ordinal)) failed.Add($"introduced-code:{m.Value}");
        }
        foreach (Match m in Number.Matches(rewrite))
        {
            if (!original.Contains(m.Value, StringComparison.Ordinal)) failed.Add($"introduced-number:{m.Value}");
        }
        foreach (Match m in EnvVar.Matches(rewrite))
        {
            if (!original.Contains(m.Value, StringComparison.Ordinal)) failed.Add($"introduced-env:{m.Value}");
        }

        // Markdown structure: code-fence count must match.
        int originalFences = Regex.Count(original, @"```");
        int rewriteFences = Regex.Count(rewrite, @"```");
        if (originalFences != rewriteFences)
        {
            failed.Add("structure:fences");
        }

        return failed.Count == 0 ? Result.Valid : new Result(false, failed);
    }
}