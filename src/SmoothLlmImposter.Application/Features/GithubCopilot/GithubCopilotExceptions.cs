namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Raised when a generic provider mutation collides with the feature-owned reserved identity: creating a
/// reserved key, or deleting one whose prefix still has a durable connection. Mapped to HTTP 409 by the Host.
/// </summary>
public sealed class GithubCopilotProviderConflictException(string message) : Exception(message);

/// <summary>
/// Raised when a reserved-key mutation arrives while the feature is disabled. It fails before any SQLite
/// access, because an uninitialized store cannot prove orphan status (D1).
/// </summary>
public sealed class GithubCopilotFeatureDisabledException(string message) : Exception(message);
