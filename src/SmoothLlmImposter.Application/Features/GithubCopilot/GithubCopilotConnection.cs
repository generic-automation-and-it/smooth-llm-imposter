namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// One GitHub Copilot account connection, shared by the Anthropic Messages and OpenAI Responses provider
/// pair of its prefix. Token fields carry ciphertext only — a plaintext access/refresh token never crosses
/// this boundary (R9).
/// </summary>
public sealed record GithubCopilotConnection(
    string Prefix,
    string ConnectionId,
    string GitHubUser,
    string ClientId,
    string ApiOrigin,
    string AccessTokenCiphertext,
    string? RefreshTokenCiphertext,
    string? ExpiresAtUtc,
    long Revision);
