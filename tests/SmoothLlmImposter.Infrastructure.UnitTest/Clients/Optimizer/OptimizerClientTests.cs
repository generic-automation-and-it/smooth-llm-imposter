using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Optimizer;
using SmoothLlmImposter.Infrastructure.Clients.Optimizer;

namespace SmoothLlmImposter.Infrastructure.UnitTest.Clients.Optimizer;

public class OptimizerClientTests
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Posts_only_system_and_single_user_message_to_configured_path()
    {
        string? sentBody = null;
        Uri? sentUri = null;
        string[] sentAuth = [];
        var handler = new CallbackHandler((_, request, ct) =>
        {
            sentBody = request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            sentUri = request.RequestUri;
            sentAuth = request.Headers.Authorization is null
                ? []
                : [request.Headers.Authorization.Scheme!, request.Headers.Authorization.Parameter!];
            return Task.FromResult(Success());
        });
        await using ServiceProvider provider = BuildProvider(handler, settings =>
        {
            settings.BaseUrl = "https://opt.test/root/";
            settings.Path = "/v1/chat/completions";
            settings.Model = "opt-model";
            settings.ApiKey = "secret-token";
        });

        string result = await provider.GetRequiredService<IOptimizerClient>().OptimizeAsync("INSTR", "hello", Ct);

        sentUri.ShouldBe(new Uri("https://opt.test/root/v1/chat/completions"));
        result.ShouldBe("rewritten");
        sentAuth.ShouldBe(["Bearer", "secret-token"]);

        using JsonDocument doc = JsonDocument.Parse(sentBody!);
        JsonElement root = doc.RootElement;
        root.GetProperty("model").GetString().ShouldBe("opt-model");
        root.GetProperty("stream").GetBoolean().ShouldBeFalse();
        JsonElement messages = root.GetProperty("messages");
        messages.GetArrayLength().ShouldBe(2);
        messages[0].GetProperty("role").GetString().ShouldBe("system");
        messages[0].GetProperty("content").GetString().ShouldBe("INSTR");
        messages[1].GetProperty("role").GetString().ShouldBe("user");
        messages[1].GetProperty("content").GetString().ShouldBe("hello");
        root.TryGetProperty("tools", out _).ShouldBeFalse();
        root.TryGetProperty("tool_choice", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Uses_default_path_when_path_blank()
    {
        Uri? sentUri = null;
        var handler = new CallbackHandler((_, request, _) =>
        {
            sentUri = request.RequestUri;
            return Task.FromResult(Success());
        });
        await using ServiceProvider provider = BuildProvider(handler, settings =>
        {
            settings.BaseUrl = "https://opt.test";
            settings.Path = "";
            settings.Model = "m";
        });

        await provider.GetRequiredService<IOptimizerClient>().OptimizeAsync("i", "u", Ct);

        sentUri.ShouldBe(new Uri("https://opt.test/v1/chat/completions"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"choices\":[]}")]
    public async Task Invalid_envelopes_throw(string body)
    {
        var handler = new CallbackHandler((_, _, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        await using ServiceProvider provider = BuildProvider(handler);

        await Should.ThrowAsync<JsonException>(() =>
            provider.GetRequiredService<IOptimizerClient>().OptimizeAsync("i", "u", Ct));
    }

    [Fact]
    public async Task Retries_5xx_and_succeeds()
    {
        var handler = new CallbackHandler((call, _, _) =>
            Task.FromResult(call < 2
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : Success()));
        await using ServiceProvider provider = BuildProvider(handler);

        string result = await provider.GetRequiredService<IOptimizerClient>().OptimizeAsync("i", "u", Ct);

        result.ShouldBe("rewritten");
        handler.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Does_not_retry_4xx()
    {
        var handler = new CallbackHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        await using ServiceProvider provider = BuildProvider(handler);

        await Should.ThrowAsync<HttpRequestException>(() =>
            provider.GetRequiredService<IOptimizerClient>().OptimizeAsync("i", "u", Ct));

        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_without_retry()
    {
        var handler = new CallbackHandler(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Success();
        });
        await using ServiceProvider provider = BuildProvider(handler, settings => settings.TimeoutSeconds = 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            provider.GetRequiredService<IOptimizerClient>().OptimizeAsync("i", "u", cts.Token));

        handler.Calls.ShouldBe(0);
    }

    private static ServiceProvider BuildProvider(
        HttpMessageHandler handler,
        Action<LlmMessagesOptions>? configure = null)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<LlmMessagesOptions>(options =>
        {
            options.BaseUrl = "https://opt.test";
            options.Model = "m";
            configure?.Invoke(options);
        });
        services.AddInfrastructure(configuration);
        services.AddHttpClient(OptimizerClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

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

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"choices":[{"message":{"content":"rewritten"}}]}""", Encoding.UTF8, "application/json")
    };
}