namespace SmoothLlmImposter.Domain.Credentials;

public sealed class OpenAiCredential : ProviderCredential
{
    public const string DialectToken = "openai";

    private OpenAiCredential()
    {
    }

    public OpenAiCredential(
        string name,
        string secretCiphertext,
        CredentialAuthScheme authScheme,
        string? baseUrlOverride)
        : base(name, secretCiphertext, authScheme, baseUrlOverride)
    {
    }

    public override string ProviderDialect => DialectToken;
}
