using Microsoft.Extensions.Options;

namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Maps the feature's exact env surface onto the bound options (R6/R10). Runs after configuration binding,
/// so <c>ENABLE_GITHUB_COPILOT</c> is authoritative over <c>GithubCopilot__Enabled</c>. A malformed flag
/// value fails startup with an actionable message instead of being silently ignored.
/// </summary>
public sealed class GithubCopilotOptionsPostConfigure : IPostConfigureOptions<GithubCopilotOptions>
{
    public const string EnabledEnvironmentVariable = "ENABLE_GITHUB_COPILOT";
    public const string ClientIdEnvironmentVariable = "GITHUB_COPILOT_CLIENT_ID";

    public void PostConfigure(string? name, GithubCopilotOptions options)
    {
        string? rawEnabled = Environment.GetEnvironmentVariable(EnabledEnvironmentVariable);
        options.Enabled = rawEnabled switch
        {
            null => false,
            _ when string.IsNullOrWhiteSpace(rawEnabled) => false,
            "1" => true,
            _ when rawEnabled.Equals("true", StringComparison.OrdinalIgnoreCase) => true,
            _ => throw new OptionsValidationException(
                GithubCopilotOptions.SectionName,
                typeof(GithubCopilotOptions),
                [$"{EnabledEnvironmentVariable}='{rawEnabled}' is not a valid feature flag; expected 'true', '1', or unset/blank (absent means disabled)."]),
        };

        string? rawClientId = Environment.GetEnvironmentVariable(ClientIdEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(rawClientId))
        {
            options.ClientId = rawClientId;
        }
    }
}
