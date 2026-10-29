using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Infrastructure.Clients.Decisions;

namespace SmoothLlmImposter.Infrastructure.UnitTest.Clients.Decisions;

public class DecisionModelClientTests
{
    private const string Section = "Imposter:DecisionsFixture";
    private const string OtherSection = "Imposter:DecisionsOther";

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Posts_only_the_json_request_to_the_configured_service()
    {
        string? sentBody = null;
        Uri? sentUri = null;
        string[] sentHeaders = [];
        var handler = new CallbackHandler(async (_, request, ct) =>
        {
            sentBody = await request.Content!.ReadAsStringAsync(ct);
            sentUri = request.RequestUri;
            sentHeaders = request.Headers.Select(static header => header.Key).ToArray();
            request.Method.ShouldBe(HttpMethod.Post);
            request.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
            return Success();
        });
        await using ServiceProvider provider = BuildProvider(handler, settings =>
        {
            settings.BaseUrl = "https://decisions.test/root/";
            settings.Path = "/v1/systemone";
        });
        var request = JsonNode.Parse("""{"model":"openjev","state":"task","questions":{}}""")!.AsObject();

        JsonObject result = await provider.GetRequiredService<IDecisionModelClient>().DecideAsync(Section, request, Ct);

        sentUri.ShouldBe(new Uri("https://decisions.test/root/v1/systemone"));
        sentBody.ShouldBe(request.ToJsonString());
        sentHeaders.ShouldNotContain("Authorization");
        sentHeaders.ShouldNotContain("x-api-key");
        result["answers"].ShouldBeOfType<JsonObject>();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"answers\":null}")]
    [InlineData("{\"answers\":[]}")]
    public async Task Invalid_envelopes_throw_without_retry(string body)
    {
        var handler = new CallbackHandler((_, _, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        await using ServiceProvider provider = BuildProvider(handler);

        await Should.ThrowAsync<JsonException>(() =>
            provider.GetRequiredService<IDecisionModelClient>().DecideAsync(Section, new JsonObject(), Ct));

        handler.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, 3)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 3)]
    [InlineData(HttpStatusCode.GatewayTimeout, 1)]
    [InlineData(HttpStatusCode.InternalServerError, 1)]
    [InlineData(HttpStatusCode.BadRequest, 1)]
    public async Task Retries_only_502_and_503_statuses(HttpStatusCode status, int expectedCalls)
    {
        var handler = new CallbackHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(status)));
        await using ServiceProvider provider = BuildProvider(handler);

        await Should.ThrowAsync<HttpRequestException>(() =>
            provider.GetRequiredService<IDecisionModelClient>().DecideAsync(Section, new JsonObject(), Ct));

        handler.Calls.ShouldBe(expectedCalls);
    }

    [Fact]
    public async Task Retries_transport_failures_and_preserves_the_payload()
    {
        var payloads = new List<string>();
        var handler = new CallbackHandler(async (call, request, ct) =>
        {
            payloads.Add(await request.Content!.ReadAsStringAsync(ct));
            if (call < 3)
            {
                throw new HttpRequestException("transport unavailable");
            }

            return Success();
        });
        await using ServiceProvider provider = BuildProvider(handler);

        await provider.GetRequiredService<IDecisionModelClient>().DecideAsync(Section, 
            JsonNode.Parse("""{"questions":{"t0":{"type":"choice"}}}""")!.AsObject(), Ct);

        handler.Calls.ShouldBe(3);
        payloads.Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public async Task One_max_attempt_builds_a_valid_pipeline_without_retry()
    {
        var handler = new CallbackHandler((_, _, _) =>
            throw new HttpRequestException("transport unavailable"));
        await using ServiceProvider provider = BuildProvider(handler, settings => settings.MaxAttempts = 1);

        await Should.ThrowAsync<HttpRequestException>(() =>
            provider.GetRequiredService<IDecisionModelClient>().DecideAsync(Section, new JsonObject(), Ct));

        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task The_attempt_timeout_covers_response_bodies_and_retries_them()
    {
        var stalledBodies = new List<StallingContent>();
        var handler = new CallbackHandler((call, _, _) =>
        {
            if (call == 3)
            {
                return Task.FromResult(Success());
            }

            var content = new StallingContent();
            stalledBodies.Add(content);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        await using ServiceProvider provider = BuildProvider(handler, settings => settings.TimeoutSeconds = 1);
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        ceiling.CancelAfter(TimeSpan.FromSeconds(10));

        await provider.GetRequiredService<IDecisionModelClient>().DecideAsync(Section, new JsonObject(), ceiling.Token);

        handler.Calls.ShouldBe(3);
        stalledBodies.ShouldAllBe(content => content.WasCancelled && content.WasDisposed);
    }

    [Fact]
    public async Task The_attempt_timeout_covers_waiting_for_headers()
    {
        var handler = new CallbackHandler(async (call, _, ct) =>
        {
            if (call < 3)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return Success();
        });
        await using ServiceProvider provider = BuildProvider(handler, settings => settings.TimeoutSeconds = 1);
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        ceiling.CancelAfter(TimeSpan.FromSeconds(10));

        await provider.GetRequiredService<IDecisionModelClient>().DecideAsync(Section, new JsonObject(), ceiling.Token);

        handler.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task Exhausted_timeouts_throw_and_release_the_gate_for_the_next_call()
    {
        var handler = new CallbackHandler(async (call, _, ct) =>
        {
            if (call < 4)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return Success();
        });
        await using ServiceProvider provider = BuildProvider(handler, settings => settings.TimeoutSeconds = 1);
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        ceiling.CancelAfter(TimeSpan.FromSeconds(10));
        var client = provider.GetRequiredService<IDecisionModelClient>();

        await Should.ThrowAsync<DecisionServiceTimeoutException>(() =>
            client.DecideAsync(Section, new JsonObject(), ceiling.Token));
        await client.DecideAsync(Section, new JsonObject(), ceiling.Token);

        handler.Calls.ShouldBe(4);
    }

    [Fact]
    public async Task Separate_scopes_share_a_single_gate_and_it_serializes_requests()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CallbackHandler(async (call, _, ct) =>
        {
            if (call == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }

            return Success();
        });
        await using ServiceProvider provider = BuildProvider(handler);
        using IServiceScope firstScope = provider.CreateScope();
        using IServiceScope secondScope = provider.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IDecisionModelClient>();
        var second = secondScope.ServiceProvider.GetRequiredService<IDecisionModelClient>();
        first.ShouldBeSameAs(second);
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        ceiling.CancelAfter(TimeSpan.FromSeconds(10));

        Task<JsonObject> firstCall = first.DecideAsync(Section, new JsonObject(), ceiling.Token);
        await entered.Task.WaitAsync(ceiling.Token);
        Task<JsonObject> secondCall = second.DecideAsync(Section, new JsonObject(), ceiling.Token);

        handler.Calls.ShouldBe(1);
        secondCall.IsCompleted.ShouldBeFalse();
        release.TrySetResult();
        await Task.WhenAll(firstCall, secondCall);
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Gate_timeout_throws_without_sending_a_second_request()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentRequests = new List<string>();
        var handler = new CallbackHandler(async (_, request, ct) =>
        {
            sentRequests.Add(await request.Content!.ReadAsStringAsync(ct));
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Success();
        });
        await using ServiceProvider provider = BuildProvider(handler, settings => settings.TimeoutSeconds = 1);
        var client = provider.GetRequiredService<IDecisionModelClient>();
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        ceiling.CancelAfter(TimeSpan.FromSeconds(10));
        using var cancelFirst = CancellationTokenSource.CreateLinkedTokenSource(ceiling.Token);
        Task<JsonObject> firstCall = client.DecideAsync(Section, new JsonObject { ["request"] = "first" }, cancelFirst.Token);
        await entered.Task.WaitAsync(ceiling.Token);

        await Should.ThrowAsync<DecisionGateTimeoutException>(() =>
            client.DecideAsync(Section, new JsonObject { ["request"] = "second" }, ceiling.Token));

        await cancelFirst.CancelAsync();
        Exception firstFailure = await Should.ThrowAsync<Exception>(() => firstCall);
        (firstFailure is OperationCanceledException or DecisionServiceTimeoutException).ShouldBeTrue();
        sentRequests.ShouldAllBe(body => body.Contains("first", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Caller_cancellation_releases_the_gate_without_retry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CallbackHandler(async (call, _, ct) =>
        {
            if (call == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return Success();
        });
        await using ServiceProvider provider = BuildProvider(handler);
        var client = provider.GetRequiredService<IDecisionModelClient>();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Task<JsonObject> firstCall = client.DecideAsync(Section, new JsonObject(), cancelled.Token);
        await entered.Task.WaitAsync(Ct);
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => firstCall);
        await client.DecideAsync(Section, new JsonObject(), Ct);
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task No_opted_in_consumer_leaves_invalid_options_inert()
    {
        var handler = new CallbackHandler((_, _, _) => Task.FromResult(Success()));
        await using ServiceProvider provider = BuildProvider(handler, settings =>
        {
            settings.BaseUrl = "invalid";
            settings.Path = "invalid";
            settings.MaxAttempts = 0;
            settings.TimeoutSeconds = -1;
        }, consumers: [new FixtureConsumer(optedIn: false)]);

        provider.GetRequiredService<IDecisionModelClient>().ShouldNotBeNull();
        using HttpClient client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(DecisionModelClient.HttpClientName);
        client.Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
        handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_non_tool_consumer_gets_the_configured_attempts_while_tool_filtering_is_disabled()
    {
        // The old profile fell back to three attempts whenever tool filtering was off (HLD 015 LADR-02).
        var handler = new CallbackHandler((_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        await using ServiceProvider provider = BuildProvider(handler, settings => settings.MaxAttempts = 2,
            consumers: [new FixtureConsumer(optedIn: false, "Imposter:DecisionsTools"), new FixtureConsumer(optedIn: true)]);

        await Should.ThrowAsync<HttpRequestException>(() =>
            provider.GetRequiredService<IDecisionModelClient>().DecideAsync(Section, NonToolRequest("a"), Ct));

        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task The_default_model_is_supplied_only_when_the_request_omits_one_and_the_request_is_not_mutated()
    {
        var sentModels = new List<string?>();
        var handler = new CallbackHandler(async (_, request, ct) =>
        {
            JsonObject sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            sentModels.Add(sent["model"]?.GetValue<string>());
            return Success();
        });
        await using ServiceProvider provider = BuildProvider(handler, settings => settings.Model = "service-default");
        var client = provider.GetRequiredService<IDecisionModelClient>();
        JsonObject withoutModel = NonToolRequest("a");
        JsonObject withModel = NonToolRequest("b");
        withModel["model"] = "consumer-choice";

        await client.DecideAsync(Section, withoutModel, Ct);
        await client.DecideAsync(Section, withModel, Ct);

        sentModels.ShouldBe(["service-default", "consumer-choice"]);
        withoutModel.ContainsKey("model").ShouldBeFalse();
    }

    [Fact]
    public async Task A_null_model_is_treated_as_omitted_and_the_request_is_not_mutated()
    {
        string? sentModel = "not-sent";
        var handler = new CallbackHandler(async (_, request, ct) =>
        {
            JsonObject sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            sentModel = sent["model"]?.GetValue<string>();
            return Success();
        });
        await using ServiceProvider provider = BuildProvider(handler, settings => settings.Model = "service-default");
        JsonObject nullModel = NonToolRequest("a");
        nullModel["model"] = null;

        await provider.GetRequiredService<IDecisionModelClient>().DecideAsync(Section, nullModel, Ct);

        sentModel.ShouldBe("service-default");
        nullModel.ContainsKey("model").ShouldBeTrue();
        nullModel["model"].ShouldBeNull();
    }

    [Fact]
    public async Task Separately_constructed_clients_share_the_process_gate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CallbackHandler(async (call, _, ct) =>
        {
            if (call == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }

            return Success();
        });
        await using ServiceProvider provider = BuildProvider(handler);
        var first = ActivatorUtilities.CreateInstance<DecisionModelClient>(provider);
        var second = ActivatorUtilities.CreateInstance<DecisionModelClient>(provider);
        first.ShouldNotBeSameAs(second);
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        ceiling.CancelAfter(TimeSpan.FromSeconds(10));

        Task<JsonObject> firstCall = first.DecideAsync(Section, NonToolRequest("first"), ceiling.Token);
        await entered.Task.WaitAsync(ceiling.Token);
        Task<JsonObject> secondCall = second.DecideAsync(Section, NonToolRequest("second"), ceiling.Token);

        handler.Calls.ShouldBe(1);
        secondCall.IsCompleted.ShouldBeFalse();
        release.TrySetResult();
        await Task.WhenAll(firstCall, secondCall);
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Each_section_calls_its_own_endpoint_with_its_own_default_model()
    {
        var sent = new List<(Uri Uri, string? Model)>();
        var handler = new CallbackHandler(async (_, request, ct) =>
        {
            JsonObject body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            lock (sent)
            {
                sent.Add((request.RequestUri!, body["model"]?.GetValue<string>()));
            }

            return Success();
        });
        await using ServiceProvider provider = BuildSections(handler,
            settings => (settings.BaseUrl, settings.Model) = ("https://tools.test", "tool-model"),
            settings => (settings.BaseUrl, settings.Path, settings.Model) = ("https://gate.test/", "/v1/gate", "gate-model"));
        var client = provider.GetRequiredService<IDecisionModelClient>();

        await client.DecideAsync(Section, NonToolRequest("a"), Ct);
        await client.DecideAsync(OtherSection, NonToolRequest("b"), Ct);

        sent.ShouldBe([
            (new Uri("https://tools.test/v1/systemone"), "tool-model"),
            (new Uri("https://gate.test/v1/gate"), "gate-model")]);
    }

    [Fact]
    public async Task Each_section_keeps_its_own_attempt_budget_through_one_resilience_pipeline()
    {
        var handler = new CallbackHandler((_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        await using ServiceProvider provider = BuildSections(handler,
            settings => (settings.BaseUrl, settings.MaxAttempts) = ("https://one.test", 1),
            settings => (settings.BaseUrl, settings.MaxAttempts) = ("https://four.test", 4));
        var client = provider.GetRequiredService<IDecisionModelClient>();

        await Should.ThrowAsync<HttpRequestException>(() => client.DecideAsync(Section, NonToolRequest("a"), Ct));
        handler.Calls.ShouldBe(1);
        await Should.ThrowAsync<HttpRequestException>(() => client.DecideAsync(OtherSection, NonToolRequest("b"), Ct));
        handler.Calls.ShouldBe(5);
    }

    [Fact]
    public async Task Each_section_keeps_its_own_attempt_timeout()
    {
        var handler = new CallbackHandler(async (call, request, ct) =>
        {
            if (request.RequestUri!.Host == "short.test" && call == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return Success();
        });
        await using ServiceProvider provider = BuildSections(handler,
            settings => (settings.BaseUrl, settings.TimeoutSeconds, settings.MaxAttempts) = ("https://short.test", 1, 1),
            settings => (settings.BaseUrl, settings.TimeoutSeconds) = ("https://long.test", 3600));
        var client = provider.GetRequiredService<IDecisionModelClient>();
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        ceiling.CancelAfter(TimeSpan.FromSeconds(10));

        await Should.ThrowAsync<DecisionServiceTimeoutException>(() =>
            client.DecideAsync(Section, NonToolRequest("a"), ceiling.Token));
        await client.DecideAsync(OtherSection, NonToolRequest("b"), ceiling.Token);

        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Sections_on_the_same_endpoint_and_model_share_one_gate()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentRequests = new List<string>();
        var handler = new CallbackHandler(async (_, request, ct) =>
        {
            sentRequests.Add(await request.Content!.ReadAsStringAsync(ct));
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Success();
        });
        await using ServiceProvider provider = BuildSections(handler,
            settings => (settings.BaseUrl, settings.Model, settings.MaxAttempts) = ("https://shared.test", "m", 1),
            settings => (settings.BaseUrl, settings.Model, settings.TimeoutSeconds) = ("https://shared.test/", "m", 1));
        var client = provider.GetRequiredService<IDecisionModelClient>();
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        ceiling.CancelAfter(TimeSpan.FromSeconds(10));
        using var cancelFirst = CancellationTokenSource.CreateLinkedTokenSource(ceiling.Token);
        Task<JsonObject> firstCall = client.DecideAsync(Section, NonToolRequest("first"), cancelFirst.Token);
        await entered.Task.WaitAsync(ceiling.Token);

        await Should.ThrowAsync<DecisionGateTimeoutException>(() =>
            client.DecideAsync(OtherSection, NonToolRequest("second"), ceiling.Token));

        await cancelFirst.CancelAsync();
        await Should.ThrowAsync<Exception>(() => firstCall);
        sentRequests.ShouldAllBe(body => body.Contains("first", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://other.test", "m")]
    [InlineData("https://shared.test", "other-model")]
    public async Task Sections_on_a_different_endpoint_or_model_run_concurrently(string otherBaseUrl, string otherModel)
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new CallbackHandler(async (_, request, ct) =>
        {
            string body = await request.Content!.ReadAsStringAsync(ct);
            (body.Contains("first", StringComparison.Ordinal) ? firstEntered : secondEntered).TrySetResult();
            await release.Task.WaitAsync(ct);
            return Success();
        });
        await using ServiceProvider provider = BuildSections(handler,
            settings => (settings.BaseUrl, settings.Model) = ("https://shared.test", "m"),
            settings => (settings.BaseUrl, settings.Model) = (otherBaseUrl, otherModel));
        var client = provider.GetRequiredService<IDecisionModelClient>();
        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        ceiling.CancelAfter(TimeSpan.FromSeconds(10));

        Task<JsonObject> firstCall = client.DecideAsync(Section, NonToolRequest("first"), ceiling.Token);
        await firstEntered.Task.WaitAsync(ceiling.Token);
        Task<JsonObject> secondCall = client.DecideAsync(OtherSection, NonToolRequest("second"), ceiling.Token);

        await secondEntered.Task.WaitAsync(ceiling.Token);
        firstCall.IsCompleted.ShouldBeFalse();
        release.TrySetResult();
        await Task.WhenAll(firstCall, secondCall);
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task An_unregistered_section_throws_without_a_request()
    {
        var handler = new CallbackHandler((_, _, _) => Task.FromResult(Success()));
        await using ServiceProvider provider = BuildProvider(handler);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            provider.GetRequiredService<IDecisionModelClient>().DecideAsync("Imposter:DecisionsUnknown", NonToolRequest("a"), Ct));

        handler.Calls.ShouldBe(0);
    }

    // A non-tool consumer's request: keyed choice questions with no tool, routing or workflow concept (HLD 015 Goal 1).
    private static JsonObject NonToolRequest(string sentinel) => new()
    {
        ["state"] = $"Ticket {sentinel}: the deploy failed twice",
        ["questions"] = JsonNode.Parse("""
            {"q1":{"type":"choice","instructions":"Should the release be retried?",
                   "criteria":{"yes":"transient failure","no":"code defect"}}}
            """)
    };

    private static ServiceProvider BuildProvider(
        HttpMessageHandler handler,
        Action<DecisionServiceOptions>? configure = null,
        IDecisionServiceConsumer[]? consumers = null)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<DecisionServiceOptions>(Section, options =>
        {
            options.BaseUrl = "https://decisions.test";
            configure?.Invoke(options);
        });
        foreach (IDecisionServiceConsumer consumer in consumers ?? [new FixtureConsumer(optedIn: true)])
        {
            services.AddSingleton(consumer);
        }

        services.AddInfrastructure(configuration);
        services.AddHttpClient(DecisionModelClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildSections(
        HttpMessageHandler handler,
        Action<DecisionServiceOptions> configureSection,
        Action<DecisionServiceOptions> configureOther)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Imposter:MaxConcurrentRequests"] = "2" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure(Section, configureSection);
        services.Configure(OtherSection, configureOther);
        services.AddSingleton<IDecisionServiceConsumer>(new FixtureConsumer(optedIn: true));
        services.AddSingleton<IDecisionServiceConsumer>(new FixtureConsumer(optedIn: true, OtherSection));
        services.AddInfrastructure(configuration);
        services.AddHttpClient(DecisionModelClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private sealed class FixtureConsumer(bool optedIn, string section = Section) : IDecisionServiceConsumer
    {
        public string Section => section;

        public bool IsOptedIn => optedIn;
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"answers":{}}""", Encoding.UTF8, "application/json")
    };

    private sealed class CallbackHandler(
        Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            callback(Interlocked.Increment(ref _calls), request, cancellationToken);
    }

    private sealed class StallingContent : HttpContent
    {
        public bool WasCancelled { get; private set; }
        public bool WasDisposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                WasCancelled = true;
                throw;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = disposing;
            base.Dispose(disposing);
        }
    }
}
