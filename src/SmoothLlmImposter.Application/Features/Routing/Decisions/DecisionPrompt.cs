using System.Text;
using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

internal static class DecisionPrompt
{
    private const int StateLimit = 2000;
    private const string ReminderOpen = "<system-reminder>";
    private const string ReminderClose = "</system-reminder>";

    public static string Extract(JsonObject root, DecisionBodyShape shape)
    {
        JsonNode? input = root[shape == DecisionBodyShape.Responses ? "input" : "messages"];
        if (shape == DecisionBodyShape.Responses && input is JsonValue)
        {
            return Compose([("user", WithoutReminders(DecisionJson.Text(input)))], [DecisionJson.Text(root["instructions"])]);
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
        if (content is JsonValue)
        {
            return WithoutReminders(DecisionJson.Text(content));
        }

        return string.Join("\n", DecisionJson.Objects(content)
            .Where(part => DecisionJson.Text(part["type"]) == "text" ||
                (shape == DecisionBodyShape.Responses && DecisionJson.Text(part["type"]) is "input_text" or "output_text"))
            .Select(part => WithoutReminders(DecisionJson.Text(part["text"])))
            .Where(text => text.Length > 0));
    }

    private static string WithoutReminders(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.StartsWith(ReminderOpen, StringComparison.Ordinal) &&
            trimmed.EndsWith(ReminderClose, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        int start = trimmed.IndexOf(ReminderOpen, StringComparison.Ordinal);
        if (start < 0)
        {
            return trimmed;
        }

        var kept = new StringBuilder();
        int cursor = 0;
        while (start >= 0)
        {
            int end = trimmed.IndexOf(ReminderClose, start, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            kept.Append(trimmed, cursor, start - cursor);
            cursor = end + ReminderClose.Length;
            start = trimmed.IndexOf(ReminderOpen, cursor, StringComparison.Ordinal);
        }

        kept.Append(trimmed, cursor, trimmed.Length - cursor);
        return kept.ToString().Trim();
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
