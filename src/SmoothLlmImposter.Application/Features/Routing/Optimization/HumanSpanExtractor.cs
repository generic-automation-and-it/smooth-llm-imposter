using System.Text;

namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// Extracts human-authored spans from a message's text, excluding harness spans and local-command
/// blocks, so only those spans are rewritten (HLD 017 LADR-03). Harness tag sets mirror HLD 014.
/// </summary>
public static class HumanSpanExtractor
{
    private static string[] HarnessTags => HarnessSpans.HarnessTags;

    private static string[] CommandTags => HarnessSpans.CommandTags;

    private static string[] LocalCommandOutputTags => HarnessSpans.LocalCommandOutputTags;

    public sealed record Segment(string Text, bool IsHuman);

    public static IReadOnlyList<Segment> Split(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<Segment>();
        }

        var ranges = new List<(int Start, int End)>();

        // Harness tag spans. Openers and closers are stack-paired so an unclosed opener stays
        // ordinary text and cannot swallow a later closed block of the same tag.
        foreach (string tag in HarnessTags)
        {
            string open = $"<{tag}>";
            string close = $"</{tag}>";
            var openers = new Stack<int>();
            int cursor = 0;
            while (true)
            {
                int nextOpen = text.IndexOf(open, cursor, StringComparison.Ordinal);
                int nextClose = text.IndexOf(close, cursor, StringComparison.Ordinal);
                if (nextOpen < 0 && nextClose < 0) break;

                if (nextOpen >= 0 && (nextClose < 0 || nextOpen < nextClose))
                {
                    openers.Push(nextOpen);
                    cursor = nextOpen + open.Length;
                    continue;
                }

                if (openers.Count > 0)
                {
                    ranges.Add((openers.Pop(), nextClose + close.Length));
                }
                cursor = nextClose + close.Length;
            }
        }

        // Command blocks followed by local-command output.
        int cmdCursor = 0;
        while (NextCommandBlock(text, cmdCursor) is (int cStart, int cEnd))
        {
            string rest = text[cEnd..].TrimStart();
            if (LocalCommandOutputTags.Any(tag => rest.StartsWith(tag, StringComparison.Ordinal)))
            {
                ranges.Add((cStart, cEnd));
            }
            cmdCursor = cEnd;
        }

        if (ranges.Count == 0)
        {
            return new[] { new Segment(text, true) };
        }

        ranges.Sort();
        var merged = new List<(int Start, int End)>();
        foreach ((int s, int e) in ranges)
        {
            if (merged.Count > 0 && s <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, e));
            }
            else
            {
                merged.Add((s, e));
            }
        }

        var segments = new List<Segment>();
        int pos = 0;
        foreach ((int s, int e) in merged)
        {
            if (s > pos)
            {
                segments.Add(new Segment(text.Substring(pos, s - pos), true));
            }
            segments.Add(new Segment(text.Substring(s, e - s), false));
            pos = e;
        }
        if (pos < text.Length)
        {
            segments.Add(new Segment(text.Substring(pos), true));
        }

        return segments;
    }

    public static string ExtractHumanText(string text) =>
        string.Join("\n", Split(text)
            .Where(s => s.IsHuman && !string.IsNullOrEmpty(s.Text))
            .Select(s => s.Text));

    public static string Reassemble(IReadOnlyList<Segment> segments, IReadOnlyList<string> rewrittenHuman)
    {
        int humanCount = segments.Count(segment => segment.IsHuman);
        if (rewrittenHuman.Count != humanCount)
        {
            throw new ArgumentException(
                $"Expected {humanCount} rewritten human segment(s), received {rewrittenHuman.Count}.",
                nameof(rewrittenHuman));
        }

        var sb = new StringBuilder();
        int humanIndex = 0;
        foreach (Segment segment in segments)
        {
            if (segment.IsHuman)
            {
                sb.Append(rewrittenHuman[humanIndex++]);
            }
            else
            {
                sb.Append(segment.Text);
            }
        }
        return sb.ToString();
    }

    private static (int Start, int End)? NextCommandBlock(string text, int from)
    {
        int searchFrom = from;
        while (true)
        {
            int start = CommandTags
                .Select(tag => text.IndexOf($"<{tag}>", searchFrom, StringComparison.Ordinal))
                .Where(position => position >= 0)
                .DefaultIfEmpty(-1)
                .Min();
            if (start < 0) return null;

            int end = start;
            while (CommandSpanEnd(text, end) is int spanEnd)
            {
                end = spanEnd;
                while (end < text.Length && char.IsWhiteSpace(text[end]))
                {
                    end++;
                }
            }

            if (end != start)
            {
                return (start, end);
            }

            // Unclosed opener: ordinary text. Skip it and keep looking for a later valid block.
            searchFrom = start + 1;
        }
    }

    private static int? CommandSpanEnd(string text, int position)
    {
        foreach (string tag in CommandTags)
        {
            string open = $"<{tag}>";
            string close = $"</{tag}>";
            if (string.CompareOrdinal(text, position, open, 0, open.Length) == 0)
            {
                int closeAt = text.IndexOf(close, position, StringComparison.Ordinal);
                if (closeAt < 0)
                {
                    return null;
                }

                int nestedOpen = text.IndexOf(open, position + open.Length, StringComparison.Ordinal);
                if (nestedOpen >= 0 && nestedOpen < closeAt)
                {
                    // This opener was superseded by a later one before any close; treat it as ordinary text.
                    return null;
                }

                return closeAt + close.Length;
            }
        }
        return null;
    }
}