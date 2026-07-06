using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;
using Polly.Timeout;
using System.Net;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Domain.Routing;
using SmoothLlmImposter.Infrastructure.Persistence.Stores;

namespace SmoothLlmImposter.Infrastructure.UnitTest;

public class DependencyInjectionTests
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_a_connection_string_the_in_memory_credential_store_is_registered()
    {
        // Stateless, key-less default: no PostgreSQL configured ⇒ credential management still works in memory.
        ServiceProvider provider = BuildProvider(connectionString: null);

        var store = provider.GetRequiredService<ICredentialStore>();

        store.ShouldBeOfType<InMemoryCredentialStore>();
        (await store.GetActiveAsync(ApiDialect.Anthropic, "anthropic-official", Ct)).ShouldBeNull();
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

    [Fact]
    public void Upstream_retry_options_use_three_fixed_delays()
    {
        var options = DependencyInjection.CreateUpstreamRetryOptions();

        options.MaxRetryAttempts.ShouldBe(3);
        options.ShouldRetryAfterHeader.ShouldBeFalse();
        DependencyInjection.GetUpstreamRetryDelay(0).ShouldBe(TimeSpan.FromSeconds(1));
        DependencyInjection.GetUpstreamRetryDelay(1).ShouldBe(TimeSpan.FromSeconds(2));
        DependencyInjection.GetUpstreamRetryDelay(2).ShouldBe(TimeSpan.FromSeconds(5));
        DependencyInjection.GetUpstreamRetryDelay(3).ShouldBeNull();
    }

    [Fact]
    public void Upstream_timeout_options_use_increasing_attempt_timeouts()
    {
        var options = DependencyInjection.CreateUpstreamTimeoutOptions();

        options.TimeoutGenerator.ShouldNotBeNull();
        DependencyInjection.GetUpstreamAttemptTimeout(0).ShouldBe(TimeSpan.FromSeconds(60));
        DependencyInjection.GetUpstreamAttemptTimeout(1).ShouldBe(TimeSpan.FromSeconds(120));
        DependencyInjection.GetUpstreamAttemptTimeout(2).ShouldBe(TimeSpan.FromSeconds(300));
        DependencyInjection.GetUpstreamAttemptTimeout(3).ShouldBe(TimeSpan.FromSeconds(600));
        DependencyInjection.GetUpstreamAttemptTimeout(4).ShouldBeNull();
    }

    [Fact]
    public async Task Upstream_retry_options_only_handle_transport_exceptions()
    {
        var options = DependencyInjection.CreateUpstreamRetryOptions();

        (await ShouldHandleAsync(options, Outcome.FromException<HttpResponseMessage>(new HttpRequestException("boom"))))
            .ShouldBeTrue();
        (await ShouldHandleAsync(options, Outcome.FromException<HttpResponseMessage>(new TimeoutRejectedException())))
            .ShouldBeTrue();
        (await ShouldHandleAsync(options, Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))))
            .ShouldBeFalse();
        (await ShouldHandleAsync(options, Outcome.FromResult(new HttpResponseMessage(HttpStatusCode.OK))))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Upstream_retry_marks_the_next_attempt_timeout_bucket()
    {
        var options = DependencyInjection.CreateUpstreamRetryOptions();
        ResilienceContext context = ResilienceContextPool.Shared.Get(Ct);

        try
        {
            DependencyInjection.GetUpstreamAttemptNumber(context).ShouldBe(0);

            await options.OnRetry!(new OnRetryArguments<HttpResponseMessage>(
                context,
                Outcome.FromException<HttpResponseMessage>(new TimeoutRejectedException()),
                0,
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(60)));

            DependencyInjection.GetUpstreamAttemptNumber(context).ShouldBe(1);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
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

    private static async ValueTask<bool> ShouldHandleAsync(
        HttpRetryStrategyOptions options,
        Outcome<HttpResponseMessage> outcome)
    {
        ResilienceContext context = ResilienceContextPool.Shared.Get();
        try
        {
            return await options.ShouldHandle(new RetryPredicateArguments<HttpResponseMessage>(context, outcome, 0));
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }
}
