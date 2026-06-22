namespace SmoothLlmImposter.Domain.Credentials;

public abstract class ProviderCredential : BaseEntity
{
    protected ProviderCredential()
    {
    }

    protected ProviderCredential(
        string providerName,
        string name,
        string secretCiphertext,
        CredentialAuthScheme authScheme,
        string? baseUrlOverride)
    {
        SetProviderName(providerName);
        Rename(name);
        RotateSecret(secretCiphertext);
        AuthScheme = authScheme;
        BaseUrlOverride = NormalizeOptional(baseUrlOverride);
    }

    public string ProviderName { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string SecretCiphertext { get; private set; } = string.Empty;

    public CredentialAuthScheme AuthScheme { get; private set; }

    public bool IsActive { get; private set; }

    public string? BaseUrlOverride { get; private set; }

    public abstract string ProviderDialect { get; }

    public void MoveToProvider(string providerName)
    {
        SetProviderName(providerName);
        Touch(DateTime.UtcNow);
    }

    public void UpdateMetadata(string name, CredentialAuthScheme authScheme, string? baseUrlOverride)
    {
        Rename(name);
        AuthScheme = authScheme;
        BaseUrlOverride = NormalizeOptional(baseUrlOverride);
        Touch(DateTime.UtcNow);
    }

    public void RotateSecret(string secretCiphertext)
    {
        if (string.IsNullOrWhiteSpace(secretCiphertext))
        {
            throw new ArgumentException("Secret ciphertext is required.", nameof(secretCiphertext));
        }

        SecretCiphertext = secretCiphertext;
        Touch(DateTime.UtcNow);
    }

    public void Activate()
    {
        IsActive = true;
        Touch(DateTime.UtcNow);
    }

    public void Deactivate()
    {
        IsActive = false;
        Touch(DateTime.UtcNow);
    }

    private void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Credential name is required.", nameof(name));
        }

        Name = name.Trim();
    }

    private void SetProviderName(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            throw new ArgumentException("Provider name is required.", nameof(providerName));
        }

        ProviderName = providerName.Trim();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
