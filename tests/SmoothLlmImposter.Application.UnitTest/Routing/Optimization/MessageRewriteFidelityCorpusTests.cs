using System.Text;
using SmoothLlmImposter.Application.Features.Routing.Optimization;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Optimization;

/// <summary>
/// HLD 017 NFR-02 measurement: a corpus of Claude Code and Codex-shaped messages, each with a rewrite that either
/// only tightens the prose or alters a protected element. Every rewrite runs through the deterministic validator
/// and through a second, independent check built a different way (whitespace tokens and character shape, no shared
/// patterns). The corpus passes only when no accepted rewrite changed a protected element.
/// </summary>
public sealed class MessageRewriteFidelityCorpusTests
{
    private static readonly string[] Catalog = ["Bash", "Read", "mcp__github__create_issue"];

    private static readonly Sample[] Corpus =
    [
        // Rewrites that only tighten prose.
        new("clean: path and code span",
            "Hey, could you please take a look at src/Features/Routing/RouteResolver.cs and tell me why `ResolveDefault` returns null when there is no default provider? I think it is probably related to the IsDefault flag. Thanks a lot!",
            "Why does `ResolveDefault` in src/Features/Routing/RouteResolver.cs return null with no default provider? Probably the IsDefault flag.",
            RejectedAs: null),
        new("clean: env var, number and version",
            "Could you please bump LLM_MESSAGES_CACHE_TTL_HOURS to 48 in docs/wiki/setup.md and also pin the SDK to 10.0.403? It would be great if you could do that soon.",
            "Bump LLM_MESSAGES_CACHE_TTL_HOURS to 48 in docs/wiki/setup.md and pin the SDK to 10.0.403.",
            RejectedAs: null),
        new("clean: negations",
            "Please do not touch the Dockerfile, and never push to main without asking me first, thanks so much!",
            "Do not touch the Dockerfile and never push to main without asking me first.",
            RejectedAs: null),
        new("clean: repeated negations kept",
            "Please kindly do not delete the cache and do not touch the Dockerfile, thanks so much.",
            "Do not delete the cache and do not touch the Dockerfile.",
            RejectedAs: null),
        new("clean: mention and slash command",
            "Hey @reviewer, when you get a chance could you run /review on this branch and let me know what you find? Appreciate it.",
            "@reviewer, run /review on this branch and tell me what you find.",
            RejectedAs: null),
        new("clean: hyphenated dollar skill",
            "Hi! I would really like you to use $imposter-who and then explain which model I am actually talking to, if that is okay.",
            "Use $imposter-who and explain which model I am actually talking to.",
            RejectedAs: null),
        new("clean: url and markdown link",
            "Please read https://example.com/docs/guide and also [the spec](https://example.com/spec) before you start, I think they explain it well.",
            "Read https://example.com/docs/guide and [the spec](https://example.com/spec) first.",
            RejectedAs: null),
        new("clean: fenced block kept verbatim",
            "Could you please run this for me and tell me exactly what happens afterwards?\n```\ncd build && npm install\n```\nThanks in advance.",
            "Run this and tell me what happens:\n```\ncd build && npm install\n```",
            RejectedAs: null),
        new("clean: catalog tool names",
            "Please use the Bash tool to list the files and then use the Read tool on the first one, thanks.",
            "Use Bash to list files, then Read the first one.",
            RejectedAs: null),
        new("clean: mcp tool name",
            "When you have time, please call mcp__github__create_issue with the title from my last message, thank you.",
            "Call mcp__github__create_issue with the title from my last message.",
            RejectedAs: null),
        new("clean: headings and list nesting",
            "# Task\n\n- first, please kindly fix the 404 handling\n  - and then, if possible, add a test\n\nThanks!",
            "# Task\n\n- fix the 404 handling\n  - add a test",
            RejectedAs: null),
        new("clean: windows path and version",
            "Please open C:\\Users\\dev\\notes.txt and check whether the second line has the version 2.1.0 in it, thanks.",
            "Open C:\\Users\\dev\\notes.txt and check the second line for version 2.1.0.",
            RejectedAs: null),

        // Rewrites that alter, drop or add a protected element.
        new("altered: negation dropped",
            "Please do not touch the Dockerfile, and never push to main without asking me first, thanks so much!",
            "Touch the Dockerfile and never push to main without asking me first.",
            RejectedAs: "negation"),
        new("altered: one of two negations dropped",
            "Please kindly do not delete the cache and do not touch the Dockerfile, thanks so much.",
            "Do not delete the cache and touch the Dockerfile.",
            RejectedAs: "negation"),
        new("altered: a negation occurrence added",
            "Please kindly do not delete the cache for this entire project, thanks so much.",
            "Do not delete the cache, not this project.",
            RejectedAs: "negation"),
        new("altered: never dropped",
            "Please do not touch the Dockerfile, and never push to main without asking me first, thanks so much!",
            "Do not touch the Dockerfile and push to main without asking me first.",
            RejectedAs: "negation"),
        new("altered: number changed",
            "Could you please bump LLM_MESSAGES_CACHE_TTL_HOURS to 48 in docs/wiki/setup.md and also pin the SDK to 10.0.403? It would be great if you could do that soon.",
            "Bump LLM_MESSAGES_CACHE_TTL_HOURS to 24 in docs/wiki/setup.md and pin the SDK to 10.0.403.",
            RejectedAs: "number"),
        new("altered: version changed",
            "Could you please bump LLM_MESSAGES_CACHE_TTL_HOURS to 48 in docs/wiki/setup.md and also pin the SDK to 10.0.403? It would be great if you could do that soon.",
            "Bump LLM_MESSAGES_CACHE_TTL_HOURS to 48 in docs/wiki/setup.md and pin the SDK to 10.0.404.",
            RejectedAs: "version"),
        new("altered: path changed",
            "Hey, could you please take a look at src/Features/Routing/RouteResolver.cs and tell me why `ResolveDefault` returns null when there is no default provider? I think it is probably related to the IsDefault flag. Thanks a lot!",
            "Why does `ResolveDefault` in src/Features/Routing/RouteSelector.cs return null with no default provider? Probably the IsDefault flag.",
            RejectedAs: "path"),
        new("altered: url changed",
            "Please read https://example.com/docs/guide and also [the spec](https://example.com/spec) before you start, I think they explain it well.",
            "Read https://example.com/docs/guides and [the spec](https://example.com/spec) first.",
            RejectedAs: "url"),
        new("altered: markdown link target changed",
            "Please read https://example.com/docs/guide and also [the spec](https://example.com/spec) before you start, I think they explain it well.",
            "Read https://example.com/docs/guide and [the spec](https://example.com/specs) first.",
            RejectedAs: "link"),
        new("altered: env var changed",
            "Could you please bump LLM_MESSAGES_CACHE_TTL_HOURS to 48 in docs/wiki/setup.md and also pin the SDK to 10.0.403? It would be great if you could do that soon.",
            "Bump LLM_MESSAGES_TTL_HOURS to 48 in docs/wiki/setup.md and pin the SDK to 10.0.403.",
            RejectedAs: "env"),
        new("altered: code span changed",
            "Hey, could you please take a look at src/Features/Routing/RouteResolver.cs and tell me why `ResolveDefault` returns null when there is no default provider? I think it is probably related to the IsDefault flag. Thanks a lot!",
            "Why does `ResolveDefaults` in src/Features/Routing/RouteResolver.cs return null with no default provider? Probably the IsDefault flag.",
            RejectedAs: "code"),
        new("altered: identifier case changed",
            "Could you please explain why getUser sometimes comes back empty for a brand new account, I really cannot work it out?",
            "Explain why getuser sometimes comes back empty for a new account, I cannot work it out.",
            RejectedAs: "identifier"),
        new("altered: mention changed",
            "Hey @reviewer, when you get a chance could you run /review on this branch and let me know what you find? Appreciate it.",
            "@reviewers, run /review on this branch and tell me what you find.",
            RejectedAs: "mention"),
        new("altered: slash command changed",
            "Hey @reviewer, when you get a chance could you run /review on this branch and let me know what you find? Appreciate it.",
            "@reviewer, run /rev on this branch and tell me what you find.",
            RejectedAs: "slash"),
        new("altered: hyphenated dollar skill changed",
            "Hi! I would really like you to use $imposter-who and then explain which model I am actually talking to, if that is okay.",
            "Use $imposter-newsession and explain which model I am actually talking to.",
            RejectedAs: "dollar"),
        new("altered: slash skill with hyphens changed",
            "Hi, whenever you are ready could you please run /git-commit-push on my current branch, thank you very much?",
            "Run /git-commit on my current branch.",
            RejectedAs: "slash"),
        new("altered: fenced block content changed",
            "Could you please run this for me and tell me exactly what happens afterwards?\n```\ncd build && npm install\n```\nThanks in advance.",
            "Run this and tell me what happens:\n```\ncd out && npm install\n```",
            RejectedAs: "code"),
        new("altered: fenced block content summarised",
            "Could you please run this for me and tell me exactly what happens afterwards?\n```\ncd build && npm install --production\n```\nThanks in advance.",
            "Run this and tell me what happens:\n```\nnpm install\n```",
            RejectedAs: "code"),
        new("altered: catalog tool name dropped",
            "Please use the Bash tool to list the files and then use the Read tool on the first one, thanks.",
            "Use Bash to list files, then open the first one.",
            RejectedAs: "name"),
        new("altered: mcp tool name dropped",
            "When you have time, please call mcp__github__create_issue with the title from my last message, thank you.",
            "Call the issue tool with the title from my last message.",
            RejectedAs: "name"),
        new("altered: mcp tool name changed",
            "When you have time, please call mcp__github__create_issue with the title from my last message, thank you.",
            "Call mcp__github__create_pull_request with the title from my last message.",
            RejectedAs: "name"),
        new("altered: heading level changed",
            "# Task\n\n- first, please kindly fix the 404 handling\n  - and then, if possible, add a test\n\nThanks!",
            "## Task\n\n- fix the 404 handling\n  - add a test",
            RejectedAs: "structure"),
        new("altered: list nesting flattened",
            "# Task\n\n- first, please kindly fix the 404 handling\n  - and then, if possible, add a test\n\nThanks!",
            "# Task\n\n- fix the 404 handling\n- add a test",
            RejectedAs: "structure"),
        new("altered: number dropped",
            "# Task\n\n- first, please kindly fix the 404 handling\n  - and then, if possible, add a test\n\nThanks!",
            "# Task\n\n- fix the missing route handling\n  - add a test",
            RejectedAs: "number"),
        new("altered: path added",
            "Hey, could you please take a look at the failing handler and tell me why it returns null when there is no default provider, thanks a lot!",
            "Why does the failing handler in /etc/passwd return null with no default provider?",
            RejectedAs: "introduced-path"),
        new("altered: negation added",
            "Hey, could you please take a look at the failing handler and tell me why it returns null when there is a default provider, thanks a lot!",
            "Why does the failing handler return null when there is not a default provider?",
            RejectedAs: "introduced-negation"),
        new("altered: windows path changed",
            "Please open C:\\Users\\dev\\notes.txt and check whether the second line has the version 2.1.0 in it, thanks.",
            "Open C:\\Users\\dev\\todo.txt and check the second line for version 2.1.0.",
            RejectedAs: "path"),
    ];

    [Fact]
    public void No_accepted_rewrite_alters_a_protected_element()
    {
        (Sample Sample, MessageRewriteValidator.Result Result)[] results = Corpus
            .Select(static sample => (sample, MessageRewriteValidator.Validate(sample.Original, sample.Rewrite, Catalog)))
            .ToArray();
        var accepted = results.Where(static item => item.Result.IsValid).ToArray();
        var rejected = results.Where(static item => !item.Result.IsValid).ToArray();

        TestContext.Current.SendDiagnosticMessage(
            $"NFR-02 corpus: {Corpus.Length} rewrites, {accepted.Length} accepted, {rejected.Length} rejected " +
            $"({(double)rejected.Length / Corpus.Length:P0} rejected).");

        // The second, independent check must not flag anything the validator accepted.
        accepted.Where(item => IndependentCheck.Altered(item.Sample.Original, item.Sample.Rewrite, Catalog))
            .Select(static item => item.Sample.Name)
            .ShouldBeEmpty("accepted rewrites that changed a protected element");

        // Every rewrite built to alter an element must be rejected, and only those.
        rejected.Where(static item => !item.Sample.Alters).Select(static item => item.Sample.Name)
            .ShouldBeEmpty("clean rewrites that were rejected");
        accepted.Where(static item => item.Sample.Alters).Select(static item => item.Sample.Name)
            .ShouldBeEmpty("altering rewrites that were accepted");

        // And rejected for the reason it was built to trip, not an incidental one.
        rejected.Where(item => !item.Result.FailedElements.Any(
                element => element.StartsWith(item.Sample.RejectedAs + ":", StringComparison.Ordinal)))
            .Select(static item => item.Sample.Name)
            .ShouldBeEmpty("rewrites rejected for a different reason than intended");
    }

    [Fact]
    public void The_independent_check_flags_every_altering_rewrite_and_no_clean_one()
    {
        // Guards the oracle itself: a check that flags nothing would make the corpus test vacuous.
        Corpus.Where(static sample => sample.Alters)
            .Where(sample => !IndependentCheck.Altered(sample.Original, sample.Rewrite, Catalog))
            .Select(static sample => sample.Name)
            .ShouldBeEmpty("altering rewrites the independent check missed");
        Corpus.Where(static sample => !sample.Alters)
            .Where(sample => IndependentCheck.Altered(sample.Original, sample.Rewrite, Catalog))
            .Select(static sample => sample.Name)
            .ShouldBeEmpty("clean rewrites the independent check flagged");
    }

    /// <summary>RejectedAs is null for a rewrite that only tightens prose, else the failure kind it must be rejected as.</summary>
    private sealed record Sample(string Name, string Original, string Rewrite, string? RejectedAs)
    {
        public bool Alters => RejectedAs is not null;
    }

    /// <summary>
    /// A second opinion built differently from the validator: whitespace tokens classified by character shape, fenced
    /// blocks compared whole, structure compared line by line. It shares no pattern with the validator.
    /// </summary>
    private static class IndependentCheck
    {
        private static readonly HashSet<string> Negations = new(StringComparer.OrdinalIgnoreCase)
        {
            "not", "no", "never", "without", "cannot", "can't", "don't", "doesn't", "didn't", "isn't", "aren't",
            "wasn't", "weren't", "won't", "wouldn't", "shouldn't", "couldn't", "mustn't", "nor", "neither", "none",
            "nothing", "nowhere", "nobody", "unless", "except"
        };

        public static bool Altered(string original, string rewrite, string[] catalog)
        {
            string[] mentioned = catalog.Where(name => Words(original).Contains(name, StringComparer.Ordinal)).ToArray();
            return !Protected(original, mentioned).SetEquals(Protected(rewrite, mentioned)) ||
                !Structure(original).SequenceEqual(Structure(rewrite));
        }

        private static readonly char[] Punctuation = ['.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '"', '\''];

        private static string[] Split(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        private static IEnumerable<string> Words(string text) => Split(text).Select(static word => word.Trim(Punctuation));

        private static HashSet<string> Protected(string text, string[] catalog)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            string[] parts = text.Split("```");
            var prose = new StringBuilder();
            for (int index = 0; index < parts.Length; index++)
            {
                if (index % 2 == 1)
                {
                    found.Add("fence:" + string.Join(' ', Split(parts[index])));
                }
                else
                {
                    prose.Append(parts[index]).Append('\n');
                }
            }

            string[] spans = prose.ToString().Split('`');
            var plain = new StringBuilder();
            for (int index = 0; index < spans.Length; index++)
            {
                if (index % 2 == 1)
                {
                    found.Add("span:" + spans[index]);
                }
                else
                {
                    plain.Append(spans[index]).Append(' ');
                }
            }

            var negationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string raw in Split(plain.ToString()))
            {
                string token = raw;
                int link = token.IndexOf("](", StringComparison.Ordinal);
                if (link >= 0)
                {
                    found.Add("link:" + token[(link + 2)..].TrimEnd(')', '.', ',', ';', '!', '?'));
                    continue;
                }

                token = token.Trim(Punctuation);
                if (token.Length == 0)
                {
                    continue;
                }

                if (Negations.Contains(token))
                {
                    // Counted, so dropping one of several occurrences changes the set.
                    string word = token.ToLowerInvariant();
                    negationCounts[word] = negationCounts.GetValueOrDefault(word) + 1;
                    found.Add($"neg:{word}#{negationCounts[word]}");
                }
                else if (IsProtectedShape(token) || catalog.Contains(token, StringComparer.Ordinal))
                {
                    found.Add("tok:" + token);
                }
            }

            return found;
        }

        private static bool IsProtectedShape(string token)
        {
            if (token[0] is '@' or '$' or '/' or '~' || token.Any(char.IsDigit) || token.Contains('/') ||
                token.Contains('\\') || token.Contains('_'))
            {
                return true;
            }

            for (int index = 1; index < token.Length - 1; index++)
            {
                if (token[index] == '.' && char.IsLetter(token[index - 1]) && char.IsLetter(token[index + 1]))
                {
                    return true;
                }
            }

            for (int index = 1; index < token.Length; index++)
            {
                if (char.IsLower(token[index - 1]) && char.IsUpper(token[index]))
                {
                    return true;
                }
            }

            return token.Length >= 3 && token.All(static character => char.IsUpper(character) || character == '_');
        }

        private static IEnumerable<string> Structure(string text)
        {
            bool inFence = false;
            foreach (string line in text.Split('\n'))
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inFence = !inFence;
                    continue;
                }

                string trimmed = line.TrimStart();
                if (inFence || trimmed.Length == 0)
                {
                    continue;
                }

                if (trimmed[0] == '#')
                {
                    yield return "H" + trimmed.TakeWhile(static character => character == '#').Count();
                }
                else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
                {
                    yield return "L" + (line.Length - trimmed.Length);
                }
            }
        }
    }
}
