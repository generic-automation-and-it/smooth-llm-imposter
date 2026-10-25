using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing.Bridge;

/// <summary>
/// Validates and compacts upstream tool-call arguments before they reach the client (HLD 016 D4,
/// spec 02 section 7.4 / spec 03 section 5.6). Non-empty broken arguments are never replaced by <c>{}</c>;
/// the only repair is closing unterminated strings, brackets and a dangling trailing comma.
/// </summary>
internal static class AnthropicChatToolArguments
{
    /// <summary>
    /// Maps a non-streaming argument source (an object, a JSON string, or absent) to compact object text.
    /// </summary>
    public static bool TryMapSource(JsonNode? source, out string compactJson)
    {
        compactJson = "{}";
        if (source is null)
        {
            return true;
        }

        if (source is JsonObject obj)
        {
            compactJson = AnthropicChatJson.Compact(obj);
            return true;
        }

        return AnthropicChatJson.String(source) is { } text && TryMapText(text, allowRepair: true, out compactJson);
    }

    /// <summary>
    /// Maps held argument text to compact object text. Empty becomes <c>{}</c> when
    /// <paramref name="allowRepair"/> is true; otherwise (a length cut) empty and invalid both fail.
    /// </summary>
    public static bool TryMapText(string text, bool allowRepair, out string compactJson)
    {
        compactJson = "{}";
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return allowRepair;
        }

        if (AnthropicChatJson.TryParseObject(trimmed) is { } parsed)
        {
            compactJson = AnthropicChatJson.Compact(parsed);
            return true;
        }

        if (!allowRepair)
        {
            return false;
        }

        return TryRepair(trimmed, out string repaired) &&
            AnthropicChatJson.TryParseObject(repaired) is { } repairedObject &&
            SetCompact(repairedObject, out compactJson);
    }

    private static bool SetCompact(JsonObject obj, out string compactJson)
    {
        compactJson = AnthropicChatJson.Compact(obj);
        return true;
    }

    /// <summary>
    /// One bounded repair attempt: close an open string literal (dropping an incomplete trailing escape),
    /// remove a single dangling trailing comma, then close open containers innermost first.
    /// </summary>
    public static bool TryRepair(string text, out string repaired)
    {
        repaired = text;
        bool inString = false;
        bool escape = false;
        var stack = new Stack<char>();

        foreach (char c in text)
        {
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (c == '\\')
                {
                    escape = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                case '[':
                    stack.Push(c);
                    break;
                case '}':
                    if (stack.Count == 0 || stack.Pop() != '{')
                    {
                        return false;
                    }
                    break;
                case ']':
                    if (stack.Count == 0 || stack.Pop() != '[')
                    {
                        return false;
                    }
                    break;
            }
        }

        string working = text;

        if (inString)
        {
            // `escape` true means the text ends on an in-progress escape (a lone trailing backslash);
            // otherwise a partial \u sequence may trail.
            working = escape ? working[..^1] : RemoveIncompleteTrailingEscape(working);
            working += '"';
        }

        working = RemoveDanglingComma(working);

        while (stack.Count > 0)
        {
            working += stack.Pop() == '{' ? '}' : ']';
        }

        repaired = working;
        return true;
    }

    private static string RemoveIncompleteTrailingEscape(string text)
    {
        if (text.EndsWith('\\') && !text.EndsWith("\\\\", StringComparison.Ordinal))
        {
            return text[..^1];
        }

        // A partial \u escape: backslash + u + 0-3 hex digits at the end.
        int i = text.Length - 1;
        int hexDigits = 0;
        while (i >= 0 && hexDigits < 4 && char.IsAsciiHexDigit(text[i]))
        {
            i--;
            hexDigits++;
        }

        if (i >= 1 && text[i] == 'u' && text[i - 1] == '\\')
        {
            return text[..(i - 1)];
        }

        return text;
    }

    private static string RemoveDanglingComma(string text)
    {
        int end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        if (end > 0 && text[end - 1] == ',')
        {
            return text.Remove(end - 1, 1);
        }

        return text;
    }
}
