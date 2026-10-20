using Microsoft.Extensions.Options;

namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Startup validation for the GitHub Copilot feature (R6/R7). An enabled feature must name a stable
/// database path; otherwise saved connections could land in a file the next start never reopens.
/// </summary>
public sealed class GithubCopilotOptionsValidator : IValidateOptions<GithubCopilotOptions>
{
    public ValidateOptionsResult Validate(string? name, GithubCopilotOptions options) =>
        options.Enabled && string.IsNullOrWhiteSpace(options.DatabasePath)
            ? ValidateOptionsResult.Fail(
                $"{GithubCopilotOptions.SectionName}:DatabasePath (env GithubCopilot__DatabasePath) is required when " +
                $"{GithubCopilotOptionsPostConfigure.EnabledEnvironmentVariable} is on; point it at persistent storage, " +
                "e.g. /data/github-copilot/copilot.db.")
            : ValidateOptionsResult.Success;
}
