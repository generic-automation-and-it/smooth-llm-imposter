using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmoothLlmImposter.Domain.Credentials;

namespace SmoothLlmImposter.Infrastructure.Persistence.Configurations;

internal sealed class AnthropicCredentialConfiguration : IEntityTypeConfiguration<AnthropicCredential>
{
    public void Configure(EntityTypeBuilder<AnthropicCredential> builder)
    {
        builder.Property(x => x.AnthropicVersion).HasMaxLength(64);
    }
}
