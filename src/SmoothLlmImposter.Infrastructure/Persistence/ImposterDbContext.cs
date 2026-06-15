using Microsoft.EntityFrameworkCore;
using SmoothLlmImposter.Domain.Credentials;

namespace SmoothLlmImposter.Infrastructure.Persistence;

public sealed class ImposterDbContext(DbContextOptions<ImposterDbContext> options) : DbContext(options)
{
    public DbSet<ProviderCredential> ProviderCredentials => Set<ProviderCredential>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ImposterDbContext).Assembly);
    }
}
