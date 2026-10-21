namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Raised when an upstream GitHub/Copilot call fails in a way the operator can retry without a new login.
/// Mapped to HTTP 502 by the Host; the message never carries a token, a device code or a raw OAuth body.
/// </summary>
public sealed class GithubCopilotUpstreamException(string message) : Exception(message);

/// <summary>
/// Raised when GitHub rejects the OAuth application itself at the device-code endpoint (unknown/disabled client
/// ID, device flow off). This is an operator input problem, not a transient upstream failure, so the Start Login
/// slice returns a field-level 400 rather than a retryable 502.
/// </summary>
public sealed class GithubCopilotClientRejectedException(string message) : Exception(message);
