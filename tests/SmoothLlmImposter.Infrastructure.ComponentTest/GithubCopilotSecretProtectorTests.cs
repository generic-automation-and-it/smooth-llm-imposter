using Shouldly;
using Xunit;
using SmoothLlmImposter.Infrastructure.Persistence.GithubCopilot;

namespace SmoothLlmImposter.Infrastructure.ComponentTest;

/// <summary>
/// L1 — the feature's isolated Data Protection key ring on real disk: ciphertext never equals plaintext,
/// a fresh protector pointed at the same directory decrypts, and a different key ring refuses.
/// </summary>
public sealed class GithubCopilotSecretProtectorTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"copilot-keys-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public void Protect_and_Unprotect_round_trip_without_storing_plaintext()
    {
        DataProtectionGithubCopilotSecretProtector protector = DataProtectionGithubCopilotSecretProtector.Create(_dataDirectory);

        string ciphertext = protector.Protect("gho_secret_token");

        ciphertext.ShouldNotBe("gho_secret_token");
        ciphertext.ShouldNotContain("gho_secret_token");
        protector.Unprotect(ciphertext).ShouldBe("gho_secret_token");
    }

    [Fact]
    public void A_reopened_protector_on_the_same_key_ring_decrypts_old_ciphertext()
    {
        string ciphertext = DataProtectionGithubCopilotSecretProtector.Create(_dataDirectory).Protect("gho_secret_token");

        // A process-restart analogue: a fresh protector built from the same on-disk key ring.
        DataProtectionGithubCopilotSecretProtector reopened = DataProtectionGithubCopilotSecretProtector.Create(_dataDirectory);

        reopened.Unprotect(ciphertext).ShouldBe("gho_secret_token");
        Directory.GetFiles(Path.Combine(_dataDirectory, "keys")).ShouldNotBeEmpty();
    }

    [Fact]
    public void A_different_key_ring_fails_clearly_instead_of_returning_garbage()
    {
        string ciphertext = DataProtectionGithubCopilotSecretProtector.Create(_dataDirectory).Protect("gho_secret_token");
        string otherDirectory = Path.Combine(Path.GetTempPath(), $"copilot-keys-other-{Guid.NewGuid():N}");

        try
        {
            DataProtectionGithubCopilotSecretProtector other = DataProtectionGithubCopilotSecretProtector.Create(otherDirectory);
            InvalidOperationException ex = Should.Throw<InvalidOperationException>(() => other.Unprotect(ciphertext));
            ex.Message.ShouldContain("key ring");
        }
        finally
        {
            if (Directory.Exists(otherDirectory))
            {
                Directory.Delete(otherDirectory, recursive: true);
            }
        }
    }
}
