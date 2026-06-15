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
        builder.Property(x => x.SecretCiphertext).IsRequired();
        builder.Property(x => x.AuthScheme).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(x => x.BaseUrlOverride).HasMaxLength(2048);
        builder.Property(x => x.CreatedAtUtc).IsRequired();
        builder.Property(x => x.UpdatedAtUtc).IsRequired();

        builder.Ignore(x => x.ProviderDialect);
        builder.HasDiscriminator<string>("ProviderDialect")
            .HasValue<OpenAiCredential>(OpenAiCredential.DialectToken)
            .HasValue<AnthropicCredential>(AnthropicCredential.DialectToken);

        builder.HasIndex("ProviderDialect", nameof(ProviderCredential.Name)).IsUnique();
    }
}
