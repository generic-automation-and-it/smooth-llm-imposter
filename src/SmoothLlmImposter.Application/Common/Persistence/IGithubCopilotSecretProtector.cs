namespace SmoothLlmImposter.Application.Common.Persistence;

/// <summary>
/// Encrypts Copilot tokens at rest with an isolated protector and key ring, so enabling this feature can
/// never invalidate credentials written by the generic <see cref="ISecretProtector"/> path (R7/D4). Only
/// ciphertext crosses the store boundary; plaintext exists only in process memory for the duration of a
/// login, refresh or inference request.
/// </summary>
public interface IGithubCopilotSecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string ciphertext);
}
