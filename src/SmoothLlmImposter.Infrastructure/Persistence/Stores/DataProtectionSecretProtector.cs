using Microsoft.AspNetCore.DataProtection;
using SmoothLlmImposter.Application.Common.Persistence;

namespace SmoothLlmImposter.Infrastructure.Persistence.Stores;

internal sealed class DataProtectionSecretProtector(IDataProtectionProvider dataProtectionProvider) : ISecretProtector
{
    private const string Purpose = "ProviderCredential.Secret";
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(Purpose);

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
