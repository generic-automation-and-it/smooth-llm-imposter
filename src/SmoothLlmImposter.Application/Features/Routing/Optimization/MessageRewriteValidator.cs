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
    private static readonly Regex DollarCommand = new(@"(?<![\w$])\$(?:[A-Za-z_][A-Za-z0-9_]*(?:-[A-Za-z0-9_]+)*|\{[A-Za-z_][A-Za-z0-9_]*\})", RegexOptions.Compiled);
    private static readonly Regex Mention = new(@"(?<![\w@])@[A-Za-z0-9_.-]+", RegexOptions.Compiled);
    private static readonly Regex DottedIdentifier = new(@"\b[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+\b", RegexOptions.Compiled);
    private static readonly Regex SnakeIdentifier = new(@"\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b", RegexOptions.Compiled);
    private static readonly Regex CamelIdentifier = new(@"\b[a-z][a-z0-9]*[A-Z][A-Za-z0-9]*\b", RegexOptions.Compiled);
    private static readonly Regex HarnessTag = new(
        "</?(?:" + string.Join("|", HarnessSpans.HarnessTags.Concat(HarnessSpans.CommandTags).Select(Regex.Escape)) + ")>",
        RegexOptions.Compiled);
    private static readonly Regex ContentWord = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    internal const double MinContentRetention = 0.6;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "if", "then", "else", "so", "than", "that", "this", "these", "those",
        "there", "here", "it", "its", "i", "me", "my", "mine", "we", "us", "our", "you", "your", "yours", "he", "she",
        "him", "her", "his", "they", "them", "their", "what", "which", "who", "whom", "whose", "when", "where", "why",
        "how", "is", "am", "are", "was", "were", "be", "been", "being", "do", "does", "did", "doing", "have", "has",
        "had", "having", "will", "would", "shall", "should", "can", "could", "may", "might", "must", "of", "in", "on",
        "at", "to", "for", "from", "by", "with", "about", "as", "into", "onto", "over", "under", "up", "down", "out",
        "off", "again", "also", "too", "very", "just", "only", "both", "each", "every", "all", "any", "some", "such",
        "own", "same", "other", "more", "most", "few", "s", "t", "d", "ll", "re", "ve", "m", "o", "y",
        "please", "kindly", "thanks", "thank", "hey", "hi", "hello", "really", "maybe", "perhaps", "probably",
        "actually", "basically", "honestly", "think", "guess", "lot", "much", "appreciate", "appreciated", "great",
        "okay", "ok", "sure", "quick", "quickly", "possible", "chance", "let", "know",
    };

    private static readonly Regex Heading = new(@"(?m)^(#{1,6})\s", RegexOptions.Compiled);
    private static readonly Regex ListItem = new(@"(?m)^([ \t]*)(?:[-*+]|\d+\.)\s", RegexOptions.Compiled);

    private static readonly (Regex Pattern, string Kind, bool IgnoreCase)[] TokenClasses =
    [
        (CodeSpan, "code", false),
        (Url, "url", false),
        (AbsolutePath, "path", false),
        (RelativePath, "path", false),
        (NestedRelativePath, "path", false),
        (WindowsPath, "path", false),
        (Number, "number", false),
        (Version, "version", false),
        (EnvVar, "env", false),
        (Negation, "negation", true),
        (SlashCommand, "slash", false),
        (DollarCommand, "dollar", false),
        (Mention, "mention", false),
        (DottedIdentifier, "identifier", false),
        (SnakeIdentifier, "identifier", false),
        (CamelIdentifier, "identifier", false),
    ];

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

        foreach ((Regex pattern, string kind, bool ignoreCase) in TokenClasses)
        {
            RequireTokens(original, rewrite, pattern, kind, failed, ignoreCase);
        }
        RequireNegationCounts(original, rewrite, failed);
        RequireMarkdownTargets(original, rewrite, failed);
        RequireContentRetention(original, rewrite, failed);

        if (protectedNames is not null)
        {
            foreach (string name in protectedNames)
            {
                if (!string.IsNullOrWhiteSpace(name) &&
                    ContainsToken(original, name, ignoreCase: false) &&
                    !ContainsToken(rewrite, name, ignoreCase: false))
                {
                    failed.Add($"name:{name}");
                }
            }
        }

        foreach ((Regex pattern, string kind, bool ignoreCase) in TokenClasses)
        {
            RejectIntroduced(original, rewrite, pattern, kind, failed, ignoreCase);
        }
        RejectIntroducedMarkdownTargets(original, rewrite, failed);

        foreach (Match tag in HarnessTag.Matches(rewrite))
        {
            if (!original.Contains(tag.Value, StringComparison.Ordinal))
            {
                failed.Add($"added-harness:{tag.Value}");
            }
        }

        foreach (string delimiter in new[] { MessageOptimizationStrategy.MessageOpenTag, MessageOptimizationStrategy.MessageCloseTag })
        {
            if (rewrite.Contains(delimiter, StringComparison.Ordinal) && !original.Contains(delimiter, StringComparison.Ordinal))
            {
                failed.Add($"added-delimiter:{delimiter}");
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
            if (!ContainsToken(rewrite, token, ignoreCase))
            {
                failed.Add($"{kind}:{token}");
            }
        }
    }

    // Presence is not enough for a negation: dropping one of "do not A and do not B" leaves a "not" behind and
    // inverts the second instruction, and adding one inverts a clause too, so each negation word must occur exactly
    // as often as it did.
    private static void RequireNegationCounts(string original, string rewrite, List<string> failed)
    {
        Dictionary<string, int> after = Negation.Matches(rewrite)
            .GroupBy(static match => match.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, Match> group in Negation.Matches(original)
                     .GroupBy(static match => match.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (after.GetValueOrDefault(group.Key) != group.Count())
            {
                failed.Add($"negation:{group.Key}");
            }
        }
    }

    private static void RequireContentRetention(string original, string rewrite, List<string> failed)
    {
        HashSet<string> before = ContentWords(original);
        if (before.Count == 0)
        {
            return;
        }

        HashSet<string> after = ContentWords(rewrite);
        int kept = before.Count(after.Contains);
        if ((double)kept / before.Count < MinContentRetention)
        {
            failed.Add($"content:kept {kept} of {before.Count} content words");
        }
    }

    private static HashSet<string> ContentWords(string text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in ContentWord.Matches(text))
        {
            string word = match.Value.ToLowerInvariant();
            if (!StopWords.Contains(word))
            {
                words.Add(Singular(word));
            }
        }
        return words;
    }

    private static string Singular(string word) =>
        word.Length > 3 && word[^1] == 's' && word[^2] != 's' ? word[..^1] : word;

    private static void RequireMarkdownTargets(string original, string rewrite, List<string> failed)
    {
        foreach (Match match in MarkdownLinkTarget.Matches(original))
        {
            string target = TrimTerminalPunctuation(match.Groups["target"].Value);
            if (!ContainsToken(rewrite, target, ignoreCase: false))
            {
                failed.Add($"link:{target}");
            }
        }
    }

    private static void RejectIntroduced(
        string original,
        string rewrite,
        Regex pattern,
        string kind,
        List<string> failed,
        bool ignoreCase)
    {
        foreach (string token in Tokens(pattern, rewrite))
        {
            if (!ContainsToken(original, token, ignoreCase))
            {
                failed.Add($"introduced-{kind}:{token}");
            }
        }
    }

    private static void RejectIntroducedMarkdownTargets(string original, string rewrite, List<string> failed)
    {
        foreach (Match match in MarkdownLinkTarget.Matches(rewrite))
        {
            string target = TrimTerminalPunctuation(match.Groups["target"].Value);
            if (!ContainsToken(original, target, ignoreCase: false))
            {
                failed.Add($"introduced-link:{target}");
            }
        }
    }

    private static bool ContainsToken(string text, string token, bool ignoreCase)
    {
        StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int index = 0;
        while (index < text.Length && (index = text.IndexOf(token, index, comparison)) >= 0)
        {
            bool startBounded = index == 0 || !IsWordChar(token[0]) || !IsWordChar(text[index - 1]);
            int end = index + token.Length;
            bool endBounded = end >= text.Length || !IsWordChar(token[^1]) || !IsWordChar(text[end]);
            if (startBounded && endBounded)
            {
                return true;
            }
            index++;
        }
        return false;
    }

    private static bool IsWordChar(char character) => char.IsLetterOrDigit(character) || character == '_';

    private static IEnumerable<string> Tokens(Regex pattern, string text) =>
        pattern.Matches(text)
            .Select(static match => TrimTerminalPunctuation(match.Value))
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal);

    private static string TrimTerminalPunctuation(string value) =>
        value.TrimEnd('.', ',', ';', ':', '!');

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
