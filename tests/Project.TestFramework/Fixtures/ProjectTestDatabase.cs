using Npgsql;
using Xunit.v3;

namespace Project.TestFramework.Fixtures;

/// <summary>
/// Factory for per-test isolated databases used in L1 Infrastructure component tests.
/// Creates a fresh database on demand against the Aspire-hosted PostgreSQL container.
/// </summary>
/// <remarks>
/// Once EF Core is wired up, extend <see cref="CreateAsync"/> to register the DbContext
/// and run migrations before returning the handle.
/// </remarks>
public sealed class ProjectTestDatabase : IAsyncDisposable
{
    private readonly string _maintenanceConnectionString;

    private ProjectTestDatabase(string connectionString, string databaseName, string maintenanceConnectionString)
    {
        ConnectionString = connectionString;
        DatabaseName = databaseName;
        _maintenanceConnectionString = maintenanceConnectionString;
    }

    public string ConnectionString { get; }

    public string DatabaseName { get; }

    public static async Task<ProjectTestDatabase> CreateAsync(
        AspireFixture aspire,
        string databaseName,
        CancellationToken cancellationToken = default,
        ITestOutputHelper? output = null)
    {
        ArgumentNullException.ThrowIfNull(aspire);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        string maintenanceConnectionString = aspire.CreateDatabaseConnectionString("postgres");
        Log(output, maintenanceConnectionString, $"Recreating database '{databaseName}'...");
        await PostgreSqlDatabaseManager.RecreateDatabaseAsync(maintenanceConnectionString, databaseName);

        string connectionString = aspire.CreateDatabaseConnectionString(databaseName);

        // TODO: Run EF Core migrations here when the application DbContext is available:
        // var services = new ServiceCollection();
        // services.AddDbContext<ProjectDbContext>(opts => opts.UseNpgsql(connectionString));
        // await using var provider = services.BuildServiceProvider();
        // await provider.MigrateProjectAsync(cancellationToken);

        Log(output, connectionString, $"Database '{databaseName}' ready.");

        return new ProjectTestDatabase(connectionString, databaseName, maintenanceConnectionString);
    }

    public async Task ResetAsync()
    {
        await PostgreSqlDatabaseManager.RecreateDatabaseAsync(_maintenanceConnectionString, DatabaseName);
        // TODO: Re-run migrations after reset when EF Core is wired up.
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void Log(ITestOutputHelper? output, string connectionString, string message)
    {
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connectionString);
            output?.WriteLine($"[ProjectTestDatabase {DateTime.UtcNow:HH:mm:ss.fff}] {b.Host}:{b.Port} — {message}");
        }
        catch (InvalidOperationException)
        {
            // Output helper is no longer active (test has ended).
        }
        catch (Exception)
        {
            try
            {
                output?.WriteLine($"[ProjectTestDatabase {DateTime.UtcNow:HH:mm:ss.fff}] {message}");
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
