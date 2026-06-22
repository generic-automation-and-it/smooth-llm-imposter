using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmoothLlmImposter.Domain.Credentials;

namespace SmoothLlmImposter.Infrastructure.Persistence.Configurations;

internal sealed class ProviderCredentialConfiguration : IEntityTypeConfiguration<ProviderCredential>
{
    public void Configure(EntityTypeBuilder<ProviderCredential> builder)
    {
        builder.ToTable("ProviderCredentials");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.ProviderName).HasMaxLength(128).IsRequired();
        builder.Property(x => x.SecretCiphertext).IsRequired();
        builder.Property(x => x.AuthScheme).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.BaseUrlOverride).HasMaxLength(2048);
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).IsRequired();

        // The CLR ProviderDialect is a computed, read-only discriminant on each subtype, so it is not a
        // mapped column. The TPH discriminator is a separate shadow property named "Dialect" — it must NOT
        // reuse the ignored CLR property's name, or EF Core's HasDiscriminator throws while reconciling the
        // ignored property (NullReferenceException in GetOrCreateDiscriminatorProperty).
        builder.Ignore(x => x.ProviderDialect);
        builder.HasDiscriminator<string>("Dialect")
            .HasValue<OpenAiCredential>(OpenAiCredential.DialectToken)
            .HasValue<AnthropicCredential>(AnthropicCredential.DialectToken);

        builder.HasIndex("Dialect", nameof(ProviderCredential.ProviderName), nameof(ProviderCredential.Name))
            .HasDatabaseName("IX_ProviderCredentials_Dialect_ProviderName_Name")
            .IsUnique();
    }
}
