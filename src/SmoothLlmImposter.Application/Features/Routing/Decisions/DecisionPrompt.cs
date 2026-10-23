using System.Text;
using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

internal static class DecisionPrompt
{
    private const int StateLimit = 2000;

    public static string Extract(JsonObject root, DecisionBodyShape shape)
    {
        JsonNode? input = root[shape == DecisionBodyShape.Responses ? "input" : "messages"];
        if (shape == DecisionBodyShape.Responses && input is JsonValue)
        {
            var state = new StringBuilder();
            Append(state, DecisionJson.Text(input));
            Append(state, DecisionJson.Text(root["instructions"]));
            return state.ToString();
        }

        if (input is not JsonArray)
        {
            return string.Empty;
        }

        JsonObject[] messages = DecisionJson.Objects(input);
        var turns = new List<(string Role, string Text)>();
        var system = new List<string>();
        foreach (JsonObject message in messages)
        {
            string role = DecisionJson.Text(message["role"]);
            if (role == "tool" || (shape == DecisionBodyShape.Responses &&
                DecisionJson.Text(message["type"]) is not ("" or "message")))
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

        int latest = turns.FindLastIndex(turn => turn.Role == "user");
        if (latest < 0)
        {
            latest = turns.Count - 1;
        }

        var result = new StringBuilder();
        for (int index = latest; index >= 0; index--)
        {
            Append(result, turns[index].Text);
        }

        foreach (string text in system)
        {
            Append(result, text);
        }

        Append(result, shape == DecisionBodyShape.Anthropic
            ? ContentText(root["system"], shape)
            : DecisionJson.Text(root["instructions"]));
        return result.ToString();
    }

    private static string ContentText(JsonNode? content, DecisionBodyShape shape)
    {
        if (content is JsonValue)
        {
            return DecisionJson.Text(content);
        }

        return string.Join("\n", DecisionJson.Objects(content)
            .Where(part => DecisionJson.Text(part["type"]) == "text" ||
                (shape == DecisionBodyShape.Responses && DecisionJson.Text(part["type"]) is "input_text" or "output_text"))
            .Select(part => DecisionJson.Text(part["text"])));
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
