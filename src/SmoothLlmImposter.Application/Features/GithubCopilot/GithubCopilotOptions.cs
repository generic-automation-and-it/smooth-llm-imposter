namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Opt-in GitHub Copilot feature configuration, bound from the <c>GithubCopilot</c> section. The feature
/// flag is the exact env var <c>ENABLE_GITHUB_COPILOT</c> (R6): absent or blank means disabled, and a
/// malformed value fails startup. The env mapping always wins over a bound <c>GithubCopilot__Enabled</c>.
/// </summary>
public sealed class GithubCopilotOptions
{
    public const string SectionName = "GithubCopilot";

    /// <summary>
    /// Feature opt-in, driven by <c>ENABLE_GITHUB_COPILOT</c> (see <see cref="GithubCopilotOptionsPostConfigure"/>).
    /// Never defaults on; disabled mode opens no Copilot database and exposes no Copilot setup APIs.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Filesystem path of the Copilot SQLite database file. Unset falls back to a per-process temp file
    /// (development only); the Docker deployment points this at the dedicated <c>/data/github-copilot</c>
    /// volume so credentials survive image replacement (R7).
    /// </summary>
    public string? DatabasePath { get; set; }

    /// <summary>
    /// Optional OAuth application client ID used as the fallback when a Start Login request omits
    /// <c>clientId</c> and the prefix has no saved ID (R10). Conventional env <c>GITHUB_COPILOT_CLIENT_ID</c>.
    /// The client ID is a public identifier, not a secret — device flow needs no client secret.
    /// </summary>
    public string? ClientId { get; set; }
}
