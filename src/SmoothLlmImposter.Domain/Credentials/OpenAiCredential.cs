namespace SmoothLlmImposter.Domain.Credentials;

public sealed class OpenAiCredential : ProviderCredential
{
    public const string DialectToken = "openai";

    private OpenAiCredential()
    {
    }

    public OpenAiCredential(
        string providerName,
        string name,
        string secretCiphertext,
        CredentialAuthScheme authScheme,
        string? baseUrlOverride)
        : base(providerName, name, secretCiphertext, authScheme, baseUrlOverride)
    {
    }

    public override string ProviderDialect => DialectToken;
}
