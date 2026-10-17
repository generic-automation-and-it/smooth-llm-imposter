using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using SmoothLlmImposter.Domain.Credentials;

namespace SmoothLlmImposter.Infrastructure.Persistence;

// EF Core is not NativeAOT-compatible (its model building and provider rely on runtime code
// generation). The AOT host fail-fasts in AddInfrastructure when an ImposterDb connection string
// is present, so this context is unreachable there — the suppressions below cover the trim/AOT
// analysis of the unreachable path only, never the JIT path (which executes it normally).
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Unreachable under NativeAOT; see class remarks.")]
[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Unreachable under NativeAOT; see class remarks.")]
public sealed class ImposterDbContext(DbContextOptions<ImposterDbContext> options) : DbContext(options)
{
    public DbSet<ProviderCredential> ProviderCredentials => Set<ProviderCredential>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ImposterDbContext).Assembly);
    }
}
