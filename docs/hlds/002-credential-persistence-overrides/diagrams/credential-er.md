# Diagram — Credential ER

Table-per-hierarchy (TPH): one table, discriminator column `ProviderDialect`. The abstract root holds all
shared fields; subtypes add only dialect-specific columns (nullable in the shared table).

```mermaid
erDiagram
    PROVIDER_CREDENTIAL {
        guid     Id PK
        datetime CreatedAtUtc
        datetime UpdatedAtUtc
        string   Name "unique per dialect"
        string   ProviderDialect "discriminator: openai | anthropic"
        string   SecretCiphertext "IDataProtector-encrypted"
        int      AuthScheme "ApiKey | Bearer"
        bool     IsActive "<=1 active per dialect"
        string   BaseUrlOverride "nullable"
        string   AnthropicVersion "nullable - anthropic subtype only"
    }
```

> There is a single physical table. `OpenAiCredential` and `AnthropicCredential` are EF Core TPH subtypes of
> the abstract `ProviderCredential : BaseEntity`; dialect-specific columns (e.g. `AnthropicVersion`) are
> nullable because they only apply to one discriminator value.
