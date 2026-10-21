using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using SmoothLlmImposter.Application.Common.Persistence;

namespace SmoothLlmImposter.Infrastructure.Persistence.GithubCopilot;

/// <summary>
/// Feature-isolated protector: its own key ring under the Copilot data directory and its own application
/// discriminator, so enabling the feature can never invalidate the generic credential key ring (R7/D4).
/// Building the provider (and creating <c>keys/</c>) is deferred to first use, so a host with the feature
/// disabled can construct its service graph — including the refresh hosted service — without touching disk.
/// </summary>
internal sealed class DataProtectionGithubCopilotSecretProtector : IGithubCopilotSecretProtector
{
    private readonly Lazy<IDataProtector> _protector;

    private DataProtectionGithubCopilotSecretProtector(string dataDirectory)
    {
        _protector = new Lazy<IDataProtector>(
            () => BuildProtector(dataDirectory),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Returns a lazy protector for a Copilot data directory. The key ring lives beside the database in
    /// <c>keys/</c>, both inside the dedicated Docker volume, so a restored volume decrypts its credentials.
    /// A private service collection keeps this key ring and application discriminator entirely separate from
    /// the host's generic Data Protection registration.
    /// </summary>
    public static DataProtectionGithubCopilotSecretProtector Create(string dataDirectory) => new(dataDirectory);

    public string Protect(string plaintext) => _protector.Value.Protect(plaintext);

    public string Unprotect(string ciphertext)
    {
        try
        {
            return _protector.Value.Unprotect(ciphertext);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                "The GitHub Copilot credential ciphertext could not be decrypted with the current key ring. " +
                "Restore the Copilot data volume (database and keys/) or start a new login.",
                ex);
        }
    }

    private static IDataProtector BuildProtector(string dataDirectory)
    {
        string keyRingPath = Path.Combine(dataDirectory, "keys");
        Directory.CreateDirectory(keyRingPath);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
            .SetApplicationName("smooth-llm-imposter-github-copilot");

        IDataProtectionProvider provider = services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
        return provider.CreateProtector("github-copilot-credentials");
    }
}
