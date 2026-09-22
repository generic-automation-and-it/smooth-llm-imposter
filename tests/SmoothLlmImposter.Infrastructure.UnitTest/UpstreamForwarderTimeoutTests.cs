using Microsoft.Extensions.Logging.Abstractions;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;
using SmoothLlmImposter.Infrastructure.Routing;

namespace SmoothLlmImposter.Infrastructure.UnitTest;

public class UpstreamForwarderTimeoutTests
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_route_timeout_is_stamped_on_the_outbound_request()
    {
        // HLD 012: this is the hop the resilience handler reads the base timeout from. Without the stamp
        // the option is configurable end-to-end and still inert, so assert the request itself carries it.
        HttpRequestMessage? sent = await ForwardAsync(Route(timeoutSeconds: 450));

        sent!.Options.TryGetValue(DependencyInjection.UpstreamTimeoutSecondsKey, out int seconds).ShouldBeTrue();
        seconds.ShouldBe(450);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task An_absent_or_non_positive_route_timeout_leaves_the_request_unstamped(int? timeoutSeconds)
    {
        HttpRequestMessage? sent = await ForwardAsync(Route(timeoutSeconds));

        sent!.Options.TryGetValue(DependencyInjection.UpstreamTimeoutSecondsKey, out _).ShouldBeFalse();
    }

    private static ProviderRoute Route(int? timeoutSeconds) => new(
        "upstream",
        ApiDialect.OpenAi,
        new Uri("https://u.example"),
        Secret: "sk-test",
        IsDefault: false,
        AnthropicVersion: null,
        Models: [],
        TimeoutSeconds: timeoutSeconds);

    private async Task<HttpRequestMessage?> ForwardAsync(ProviderRoute route)
    {
        var handler = new CapturingHandler();
        var forwarder = new UpstreamForwarder(
            new SingleClientFactory(handler),
            NullLogger<UpstreamForwarder>.Instance);

        using HttpResponseMessage response = await forwarder.SendAsync(
            new RouteDecision(route, "target-model", CachingEnabled: false, IsImposter: true),
            credentialOverride: null,
            ApiDialect.OpenAi,
            HttpMethod.Post,
            body: "{}",
            path: "/v1/responses",
            queryString: null,
            CallerHeaders.None,
            sessionIdentity: null,
            Ct);

        return handler.Sent;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Sent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
