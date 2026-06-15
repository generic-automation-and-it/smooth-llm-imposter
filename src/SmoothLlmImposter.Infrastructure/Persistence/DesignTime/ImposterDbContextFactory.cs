using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SmoothLlmImposter.Infrastructure.Persistence.DesignTime;

public sealed class ImposterDbContextFactory : IDesignTimeDbContextFactory<ImposterDbContext>
{
    public ImposterDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ImposterDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=smoothllmimposter;Username=postgres;Password=postgres")
            .Options;

        return new ImposterDbContext(options);
    }
}
