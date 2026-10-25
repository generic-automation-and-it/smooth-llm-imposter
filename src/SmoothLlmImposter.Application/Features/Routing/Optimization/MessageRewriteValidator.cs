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
    private static readonly Regex Url = new(@"\b(?:https?|ftp)://[^\s)>\]]+|mailto:[^\s)]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MarkdownLinkTarget = new(@"!?\[[^\]]*\]\(\s*(?<target>[^)\s]+)", RegexOptions.Compiled);
    private static readonly Regex AbsolutePath = new(@"(?<![\w:/])(?:/(?:[A-Za-z0-9_.-]+/)+[A-Za-z0-9_.-]+|~/[^\s]+|\.{1,2}/[^\s]+)", RegexOptions.Compiled);
    private static readonly Regex RelativePath = new(@"(?<![\w:/])(?:[A-Za-z0-9_.-]+/){1,}[A-Za-z0-9_.-]+\.[A-Za-z0-9]{1,10}\b", RegexOptions.Compiled);
    private static readonly Regex NestedRelativePath = new(@"(?<![\w:/])(?:[A-Za-z0-9_.-]+/){2,}[A-Za-z0-9_.-]+", RegexOptions.Compiled);
    private static readonly Regex WindowsPath = new(@"\b[A-Za-z]:\\[^\s]+", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"\b\d+(?:\.\d+){0,3}\b", RegexOptions.Compiled);
    private static readonly Regex Version = new(@"\bv?\d+\.\d+(?:\.\d+)?(?:[-+][0-9A-Za-z.]+)?\b", RegexOptions.Compiled);
    private static readonly Regex EnvVar = new(@"\b[A-Z][A-Z0-9_]{2,}\b", RegexOptions.Compiled);
    private static readonly Regex Negation = new(
        @"\b(?:not|no|never|without|cannot|can't|don't|doesn't|didn't|isn't|aren't|wasn't|weren't|won't|wouldn't|shouldn't|couldn't|mustn't|nor|neither|none|nothing|nowhere|nobody|hardly|barely|scarcely|unless|except|excluding)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SlashCommand = new(@"(?<![\w:/])/[A-Za-z][A-Za-z0-9_-]*", RegexOptions.Compiled);
    private static readonly Regex DollarCommand = new(@"(?<![\w$])\$(?:[A-Za-z_][A-Za-z0-9_]*|\{[A-Za-z_][A-Za-z0-9_]*\})", RegexOptions.Compiled);
    private static readonly Regex Mention = new(@"(?<![\w@])@[A-Za-z0-9_.-]+", RegexOptions.Compiled);
    private static readonly Regex DottedIdentifier = new(@"\b[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+\b", RegexOptions.Compiled);
    private static readonly Regex SnakeIdentifier = new(@"\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b", RegexOptions.Compiled);
    private static readonly Regex CamelIdentifier = new(@"\b[a-z][a-z0-9]*[A-Z][A-Za-z0-9]*\b", RegexOptions.Compiled);
    private static readonly Regex HarnessTag = new(
        "</?(?:" + string.Join("|", HarnessSpans.HarnessTags.Concat(HarnessSpans.CommandTags).Select(Regex.Escape)) + ")>",
        RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"(?m)^(#{1,6})\s", RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"(?m)^([ \t]*)(?:[-*+]|\d+\.)\s", RegexOptions.Compiled);

    public static Result Validate(
        string original,
        string rewrite,
        IReadOnlyCollection<string>? protectedNames = null,
        string? instructionText = null)
    {
        if (original is null || rewrite is null)
        {
            return new Result(false, new[] { "null-input" });
        }

        var failed = new List<string>();

        if (string.IsNullOrEmpty(rewrite) || rewrite.Length >= original.Length)
        {
            failed.Add("length");
        }

        RequireTokens(original, rewrite, CodeSpan, "code", failed);
        RequireTokens(original, rewrite, Url, "url", failed);
        RequireMarkdownTargets(original, rewrite, failed);
        RequireTokens(original, rewrite, AbsolutePath, "path", failed);
        RequireTokens(original, rewrite, RelativePath, "path", failed);
        RequireTokens(original, rewrite, NestedRelativePath, "path", failed);
        RequireTokens(original, rewrite, WindowsPath, "path", failed);
        RequireTokens(original, rewrite, Number, "number", failed);
        RequireTokens(original, rewrite, Version, "version", failed);
        RequireTokens(original, rewrite, EnvVar, "env", failed);
        RequireTokens(original, rewrite, Negation, "negation", failed, ignoreCase: true);
        RequireTokens(original, rewrite, SlashCommand, "slash", failed);
        RequireTokens(original, rewrite, DollarCommand, "dollar", failed);
        RequireTokens(original, rewrite, Mention, "mention", failed);
        RequireTokens(original, rewrite, DottedIdentifier, "identifier", failed);
        RequireTokens(original, rewrite, SnakeIdentifier, "identifier", failed);
        RequireTokens(original, rewrite, CamelIdentifier, "identifier", failed);

        if (protectedNames is not null)
        {
            foreach (string name in protectedNames)
            {
                if (!string.IsNullOrWhiteSpace(name) &&
                    original.Contains(name, StringComparison.Ordinal) &&
                    !rewrite.Contains(name, StringComparison.Ordinal))
                {
                    failed.Add($"name:{name}");
                }
            }
        }

        RejectIntroduced(original, rewrite, CodeSpan, "code", failed);
        RejectIntroduced(original, rewrite, Url, "url", failed);
        RejectIntroducedMarkdownTargets(original, rewrite, failed);
        RejectIntroduced(original, rewrite, AbsolutePath, "path", failed);
        RejectIntroduced(original, rewrite, RelativePath, "path", failed);
        RejectIntroduced(original, rewrite, NestedRelativePath, "path", failed);
        RejectIntroduced(original, rewrite, WindowsPath, "path", failed);
        RejectIntroduced(original, rewrite, Number, "number", failed);
        RejectIntroduced(original, rewrite, Version, "version", failed);
        RejectIntroduced(original, rewrite, EnvVar, "env", failed);
        RejectIntroduced(original, rewrite, SlashCommand, "slash", failed);
        RejectIntroduced(original, rewrite, DollarCommand, "dollar", failed);
        RejectIntroduced(original, rewrite, Mention, "mention", failed);
        RejectIntroduced(original, rewrite, DottedIdentifier, "identifier", failed);
        RejectIntroduced(original, rewrite, SnakeIdentifier, "identifier", failed);
        RejectIntroduced(original, rewrite, CamelIdentifier, "identifier", failed);

        foreach (Match tag in HarnessTag.Matches(rewrite))
        {
            if (!original.Contains(tag.Value, StringComparison.Ordinal))
            {
                failed.Add($"added-harness:{tag.Value}");
            }
        }

        if (!string.IsNullOrWhiteSpace(instructionText))
        {
            foreach (string line in instructionText.Split('\n').Select(static part => part.Trim()))
            {
                if (line.Length >= 20 && rewrite.Contains(line, StringComparison.Ordinal))
                {
                    failed.Add("added-instruction");
                    break;
                }
            }
        }

        RequireSameStructure(original, rewrite, Heading, "headings", failed);
        RequireSameStructure(original, rewrite, ListItem, "list-nesting", failed, matchGroup: 1);
        if (Regex.Count(original, "```") != Regex.Count(rewrite, "```"))
        {
            failed.Add("structure:fences");
        }

        return failed.Count == 0
            ? Result.Valid
            : new Result(false, failed.Distinct().ToArray());
    }

    /// <summary>
    /// One targeted retry naming the failed elements, then the original is forwarded (LADR-04).
    /// Caller cancellation propagates; any other retry failure forwards the original.
    /// </summary>
    public static async Task<string> ValidateWithRetryAsync(
        string original,
        string rewrite,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<string>> retry,
        IReadOnlyCollection<string>? protectedNames = null,
        string? instructionText = null,
        CancellationToken cancellationToken = default)
    {
        Result first = Validate(original, rewrite, protectedNames, instructionText);
        if (first.IsValid)
        {
            return rewrite;
        }

        string retried;
        try
        {
            retried = await retry(original, first.FailedElements, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return original;
        }

        return Validate(original, retried, protectedNames, instructionText).IsValid
            ? retried
            : original;
    }

    private static void RequireTokens(
        string original,
        string rewrite,
        Regex pattern,
        string kind,
        List<string> failed,
        bool ignoreCase = false)
    {
        foreach (string token in Tokens(pattern, original))
        {
            bool present = ignoreCase
                ? rewrite.Contains(token, StringComparison.OrdinalIgnoreCase)
                : rewrite.Contains(token, StringComparison.Ordinal);
            if (!present)
            {
                failed.Add($"{kind}:{token}");
            }
        }
    }

    private static void RequireMarkdownTargets(string original, string rewrite, List<string> failed)
    {
        foreach (Match match in MarkdownLinkTarget.Matches(original))
        {
            string target = match.Groups["target"].Value;
            if (!rewrite.Contains(target, StringComparison.Ordinal))
            {
                failed.Add($"link:{target}");
            }
        }
    }

    private static void RejectIntroduced(string original, string rewrite, Regex pattern, string kind, List<string> failed)
    {
        foreach (string token in Tokens(pattern, rewrite))
        {
            if (!original.Contains(token, StringComparison.Ordinal))
            {
                failed.Add($"introduced-{kind}:{token}");
            }
        }
    }

    private static void RejectIntroducedMarkdownTargets(string original, string rewrite, List<string> failed)
    {
        foreach (Match match in MarkdownLinkTarget.Matches(rewrite))
        {
            string target = match.Groups["target"].Value;
            if (!original.Contains(target, StringComparison.Ordinal))
            {
                failed.Add($"introduced-link:{target}");
            }
        }
    }

    private static IEnumerable<string> Tokens(Regex pattern, string text) =>
        pattern.Matches(text).Select(static match => match.Value).Distinct(StringComparer.Ordinal);

    private static void RequireSameStructure(
        string original,
        string rewrite,
        Regex pattern,
        string kind,
        List<string> failed,
        int matchGroup = 0)
    {
        string[] before = pattern.Matches(original).Select(match => match.Groups[matchGroup].Value).ToArray();
        string[] after = pattern.Matches(rewrite).Select(match => match.Groups[matchGroup].Value).ToArray();
        if (!before.SequenceEqual(after, StringComparer.Ordinal))
        {
            failed.Add($"structure:{kind}");
        }
    }
}
