using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Domain.Routing;
using SmoothLlmImposter.Infrastructure.Persistence.Stores;

namespace SmoothLlmImposter.Infrastructure.UnitTest;

public class DependencyInjectionTests
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_a_connection_string_the_no_op_credential_store_is_registered()
    {
        // Stateless, key-less default: no PostgreSQL configured ⇒ the router must boot and the catch-all
        // passthrough must resolve a null credential rather than crash building/opening an EF model.
        ServiceProvider provider = BuildProvider(connectionString: null);

        var store = provider.GetRequiredService<ICredentialStore>();

        store.ShouldBeOfType<NullCredentialStore>();
        (await store.GetActiveAsync(ApiDialect.Anthropic, Ct)).ShouldBeNull();
        (await store.ListAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public void With_a_connection_string_the_ef_backed_credential_store_is_registered()
    {
        ServiceProvider provider = BuildProvider(
            connectionString: "Host=localhost;Port=5432;Database=smoothllmimposter;Username=postgres;Password=postgres");

        using IServiceScope scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ICredentialStore>();

        store.ShouldBeOfType<CredentialStore>();
    }

    private static ServiceProvider BuildProvider(string? connectionString)
    {
        Dictionary<string, string?> settings = [];
        if (connectionString is not null)
        {
            settings["ConnectionStrings:ImposterDb"] = connectionString;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new ServiceCollection()
            .AddLogging()
            .AddInfrastructure(configuration)
            .BuildServiceProvider();
    }
}
