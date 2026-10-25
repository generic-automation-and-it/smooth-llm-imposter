using System.Text;

namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// Extracts human-authored spans from a message's text, excluding harness spans and local-command
/// blocks, so only those spans are rewritten (HLD 017 LADR-03). Harness tag sets mirror HLD 014.
/// </summary>
public static class HumanSpanExtractor
{
    private static readonly string[] HarnessTags =
        ["system-reminder", "system_instruction", "local-command-caveat", "local-command-stdout", "local-command-stderr"];

    private static readonly string[] CommandTags = ["command-name", "command-message", "command-args"];

    private static readonly string[] LocalCommandOutputTags = ["<local-command-stdout>", "<local-command-stderr>"];

    public sealed record Segment(string Text, bool IsHuman);

    public static IReadOnlyList<Segment> Split(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<Segment>();
        }

        var ranges = new List<(int Start, int End)>();

        // Harness tag spans (closed).
        foreach (string tag in HarnessTags)
        {
            string open = $"<{tag}>";
            string close = $"</{tag}>";
            int cursor = 0;
            while (true)
            {
                int start = text.IndexOf(open, cursor, StringComparison.Ordinal);
                if (start < 0) break;
                int end = text.IndexOf(close, start, StringComparison.Ordinal);
                if (end < 0) break;
                ranges.Add((start, end + close.Length));
                cursor = end + close.Length;
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
        string.Concat(Split(text).Where(s => s.IsHuman).Select(s => s.Text));

    public static string Reassemble(IReadOnlyList<Segment> segments, IReadOnlyList<string> rewrittenHuman)
    {
        var sb = new StringBuilder();
        int humanIndex = 0;
        foreach (Segment segment in segments)
        {
            if (segment.IsHuman)
            {
                sb.Append(humanIndex < rewrittenHuman.Count ? rewrittenHuman[humanIndex] : segment.Text);
                humanIndex++;
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
        int start = CommandTags
            .Select(tag => text.IndexOf($"<{tag}>", from, StringComparison.Ordinal))
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
        return end == start ? null : (start, end);
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
                return closeAt < 0 ? null : closeAt + close.Length;
            }
        }
        return null;
    }
}