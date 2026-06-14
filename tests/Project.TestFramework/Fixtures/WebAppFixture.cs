using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit.v3;

namespace Project.TestFramework.Fixtures;

/// <summary>
/// Generic in-process web-host fixture built on <see cref="WebApplicationFactory{TProgram}"/> and
/// backed by <see cref="AspireFixture"/> (PostgreSQL + WireMock containers).
/// Close it for a Host's entry point in an integration test:
/// <c>public sealed class HostWebAppFixture : WebAppFixture&lt;Program&gt;;</c>
/// Override <see cref="EnrichConfigurationAsync"/> to inject app-specific connection strings and
/// external-service URLs; override <see cref="PostInitializeAsync"/> to run post-boot setup.
/// </summary>
public abstract class WebAppFixture<TProgram> : IAsyncLifetime
    where TProgram : class
{
    private readonly AspireFixture _aspire = new();
    private WebApplicationFactory<TProgram>? _factory;
    private IServiceScope? _serviceScope;

    public HttpClient HttpClient { get; private set; } = default!;

    public IServiceProvider Services { get; private set; } = default!;

    protected AspireFixture Aspire => _aspire;

    protected virtual string DatabaseName => $"host-integration-{Guid.NewGuid():N}";

    protected virtual bool RemoveHostedServices => true;

    protected virtual bool RecreateDatabaseOnInitialize => false;

    protected virtual Dictionary<string, string?> ConfigurationOverrides => [];

    public void SetOutput(ITestOutputHelper? output) => _aspire.SetOutput(output);

    public async ValueTask InitializeAsync()
    {
        await _aspire.InitializeAsync();

        if (RecreateDatabaseOnInitialize)
        {
            string maintenance = _aspire.CreateDatabaseConnectionString("postgres");
            await PostgreSqlDatabaseManager.RecreateDatabaseAsync(maintenance, DatabaseName);
        }

        Dictionary<string, string?> overrides = new(ConfigurationOverrides);
        await EnrichConfigurationAsync(overrides);

        _factory = new WebApplicationFactory<TProgram>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    if (RemoveHostedServices)
                    {
                        services.RemoveAll<IHostedService>();
                    }
                });

                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(overrides));
            });

        HttpClient = _factory.CreateClient();
        _serviceScope = _factory.Services.CreateScope();
        Services = _serviceScope.ServiceProvider;

        await PostInitializeAsync();
    }

    /// <summary>Override to inject app-specific configuration (connection strings, WireMock URL, etc.).</summary>
    protected virtual Task EnrichConfigurationAsync(Dictionary<string, string?> overrides) => Task.CompletedTask;

    /// <summary>Override to run post-boot setup (e.g. trigger a sync cycle before tests run).</summary>
    protected virtual Task PostInitializeAsync() => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        HttpClient?.Dispose();
        _serviceScope?.Dispose();

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _aspire.DisposeAsync();
    }
}
