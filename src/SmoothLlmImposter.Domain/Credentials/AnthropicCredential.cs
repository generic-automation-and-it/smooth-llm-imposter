namespace SmoothLlmImposter.Domain.Credentials;

public sealed class AnthropicCredential : ProviderCredential
{
    public const string DialectToken = "anthropic";

    private AnthropicCredential()
    {
    }

    public AnthropicCredential(
        string name,
        string secretCiphertext,
        CredentialAuthScheme authScheme,
        string? baseUrlOverride,
        string? anthropicVersion)
        : base(name, secretCiphertext, authScheme, baseUrlOverride)
    {
        AnthropicVersion = string.IsNullOrWhiteSpace(anthropicVersion) ? null : anthropicVersion.Trim();
    }

    public string? AnthropicVersion { get; private set; }

    public override string ProviderDialect => DialectToken;

    public void SetAnthropicVersion(string? anthropicVersion)
    {
        AnthropicVersion = string.IsNullOrWhiteSpace(anthropicVersion) ? null : anthropicVersion.Trim();
        Touch(DateTime.UtcNow);
    }
}
