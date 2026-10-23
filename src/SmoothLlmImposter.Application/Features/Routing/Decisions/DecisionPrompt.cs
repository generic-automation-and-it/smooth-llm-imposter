using System.Text;
using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

internal static class DecisionPrompt
{
    private const int StateLimit = 2000;

    private static readonly string[] HarnessTags =
        ["system-reminder", "system_instruction", "local-command-caveat", "local-command-stdout", "local-command-stderr"];

    private static readonly string[] CommandTags = ["command-name", "command-message", "command-args"];

    private static readonly string[] LocalCommandOutputTags = ["<local-command-stdout>", "<local-command-stderr>"];

    public static string Extract(JsonObject root, DecisionBodyShape shape)
    {
        JsonNode? input = root[shape == DecisionBodyShape.Responses ? "input" : "messages"];
        if (shape == DecisionBodyShape.Responses && input is JsonValue)
        {
            return Compose([("user", ContentText(input, shape))], [DecisionJson.Text(root["instructions"])]);
        }

        if (input is not JsonArray)
        {
            return string.Empty;
        }

        var turns = new List<(string Role, string Text)>();
        var system = new List<string>();
        foreach (JsonObject message in DecisionJson.Objects(input))
        {
            string role = DecisionJson.Text(message["role"]);
            if (role == "tool" || (shape == DecisionBodyShape.Responses &&
                DecisionJson.Text(message["type"]) is not ("" or "message")) || CarriesToolResult(message, shape))
            {
                continue;
            }

            string text = ContentText(message["content"], shape);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (role is "system" or "developer")
            {
                system.Add(text);
            }
            else if (role is "user" or "assistant")
            {
                turns.Add((role, text));
            }
        }

        system.Add(shape == DecisionBodyShape.Anthropic
            ? ContentText(root["system"], shape)
            : DecisionJson.Text(root["instructions"]));
        return Compose(turns, system);
    }

    private static string Compose(List<(string Role, string Text)> turns, List<string> system)
    {
        var state = new StringBuilder();
        int latest = turns.FindLastIndex(turn => turn.Role == "user");
        if (latest < 0)
        {
            latest = turns.Count - 1;
        }

        if (latest < 0 || string.IsNullOrWhiteSpace(turns[latest].Text))
        {
            string context = string.Join("\n", system.Where(text => !string.IsNullOrWhiteSpace(text)));
            Append(state, context.Length == 0 ? string.Empty : $"Context:\n{context}");
            return state.ToString();
        }

        Append(state, $"User request:\n{turns[latest].Text}");
        if (latest > 0)
        {
            Append(state, "\nEarlier conversation (newest first):");
        }

        for (int index = latest - 1; index >= 0; index--)
        {
            Append(state, $"{(turns[index].Role == "user" ? "User" : "Assistant")}: {turns[index].Text}");
        }

        return state.ToString();
    }

    private static bool CarriesToolResult(JsonObject message, DecisionBodyShape shape) =>
        shape == DecisionBodyShape.Anthropic &&
        DecisionJson.Objects(message["content"]).Any(block => DecisionJson.Text(block["type"]) == "tool_result");

    private static string ContentText(JsonNode? content, DecisionBodyShape shape)
    {
        string[] parts = content is JsonValue
            ? [DecisionJson.Text(content)]
            : DecisionJson.Objects(content)
                .Where(part => DecisionJson.Text(part["type"]) == "text" ||
                    (shape == DecisionBodyShape.Responses && DecisionJson.Text(part["type"]) is "input_text" or "output_text"))
                .Select(part => DecisionJson.Text(part["text"]))
                .ToArray();
        return string.Join("\n", WithoutLocalCommandBlocks(parts)
            .Select(WithoutHarnessText)
            .Where(text => text.Length > 0));
    }

    private static string[] WithoutLocalCommandBlocks(string[] parts)
    {
        var result = new string[parts.Length];
        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index];
            var kept = new StringBuilder();
            int cursor = 0;
            while (NextCommandBlock(part, cursor) is (int start, int end))
            {
                kept.Append(part, cursor, start - cursor);
                if (!IsFollowedByLocalCommandOutput(part[end..], parts.AsSpan(index + 1)))
                {
                    kept.Append(part, start, end - start);
                }

                cursor = end;
            }

            kept.Append(part, cursor, part.Length - cursor);
            result[index] = kept.ToString();
        }

        return result;
    }

    private static (int Start, int End)? NextCommandBlock(string text, int from)
    {
        int start = CommandTags
            .Select(tag => text.IndexOf($"<{tag}>", from, StringComparison.Ordinal))
            .Where(position => position >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        if (start < 0)
        {
            return null;
        }

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

    private static bool IsFollowedByLocalCommandOutput(string rest, ReadOnlySpan<string> following)
    {
        string next = rest.TrimStart();
        foreach (string part in following)
        {
            if (next.Length > 0)
            {
                break;
            }

            next = part.TrimStart();
        }

        return LocalCommandOutputTags.Any(tag => next.StartsWith(tag, StringComparison.Ordinal));
    }

    private static string WithoutHarnessText(string text)
    {
        string result = text.Trim();
        foreach (string tag in HarnessTags)
        {
            result = WithoutSpans(result, tag);
        }

        return result;
    }

    private static string WithoutSpans(string text, string tag)
    {
        string open = $"<{tag}>";
        string close = $"</{tag}>";
        int start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return text;
        }

        var kept = new StringBuilder();
        int cursor = 0;
        while (start >= 0)
        {
            int end = text.IndexOf(close, start, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            kept.Append(text, cursor, start - cursor);
            cursor = end + close.Length;
            start = text.IndexOf(open, cursor, StringComparison.Ordinal);
        }

        kept.Append(text, cursor, text.Length - cursor);
        string residue = kept.ToString().Trim();
        bool quotesClosingTag = residue.Contains(close, StringComparison.Ordinal);
        bool isWholeBlock = text.StartsWith(open, StringComparison.Ordinal) && text.EndsWith(close, StringComparison.Ordinal);
        return quotesClosingTag && isWholeBlock ? string.Empty : residue;
    }

    private static void Append(StringBuilder state, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || state.Length >= StateLimit)
        {
            return;
        }

        if (state.Length > 0)
        {
            state.Append('\n');
        }

        state.Append(text.AsSpan(0, Math.Min(text.Length, StateLimit - state.Length)));
    }
}
