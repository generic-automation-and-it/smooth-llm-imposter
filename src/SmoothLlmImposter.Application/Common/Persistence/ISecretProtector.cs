namespace SmoothLlmImposter.Application.Common.Persistence;

public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string ciphertext);
}
