namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

/// <summary>Operator-supplied labels (models, endpoints) with control characters replaced, so a value cannot forge log lines.</summary>
internal static class LogLabel
{
    public static string Safe(string label) =>
        new(label.Select(static character => char.IsControl(character) ? '?' : character).ToArray());
}
