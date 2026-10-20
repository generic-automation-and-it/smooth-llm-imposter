namespace SmoothLlmImposter.Infrastructure.Persistence.GithubCopilot;

/// <summary>
/// Mapped row for the Copilot connection store. Tracks one connection per normalized prefix, shared by
/// its Anthropic/OpenAI provider pair. Token columns hold ciphertext only; an access/refresh token is
/// never stored or returned in plaintext.
/// </summary>
internal sealed record GithubCopilotConnectionRow(
    string Prefix,
    string ConnectionId,
    string GitHubUser,
    string ClientId,
    string ApiOrigin,
    string AccessTokenCiphertext,
    string? RefreshTokenCiphertext,
    string? ExpiresAtUtc,
    long Revision);
