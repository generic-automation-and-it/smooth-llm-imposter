namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// The single tag set shared by HLD 014 state extraction and HLD 017 human-span extraction, so the two
/// stages cannot drift (HLD 017 LADR-03).
/// </summary>
internal static class HarnessSpans
{
    internal static readonly string[] HarnessTags =
        ["system-reminder", "system_instruction", "local-command-caveat", "local-command-stdout", "local-command-stderr"];

    internal static readonly string[] CommandTags = ["command-name", "command-message", "command-args"];

    internal static readonly string[] LocalCommandOutputTags = ["<local-command-stdout>", "<local-command-stderr>"];
}
