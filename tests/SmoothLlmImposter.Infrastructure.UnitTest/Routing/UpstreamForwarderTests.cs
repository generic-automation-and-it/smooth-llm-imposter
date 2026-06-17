using Microsoft.Extensions.Logging.Abstractions;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;
using SmoothLlmImposter.Infrastructure.Routing;

namespace SmoothLlmImposter.Infrastructure.UnitTest.Routing;

public class UpstreamForwarderTests
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Force_bearer_sends_authorization_bearer_and_no_x_api_key_even_for_apikey_scheme()
    {
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);
        var credentialOverride = new RouteCredentialOverride("stored-secret", CredentialAuthScheme.ApiKey, BaseUrlOverride: null, AnthropicVersion: null, ForceBearer: true);

        await forwarder.SendAsync(Decision(ApiDialect.Anthropic), credentialOverride, ApiDialect.Anthropic, "{}", "/v1/messages", queryString: null, Ct);

        capture.Authorization.ShouldBe("Bearer stored-secret");
        capture.ApiKey.ShouldBeNull();
    }

    [Fact]
    public async Task Without_force_bearer_an_apikey_scheme_credential_still_sends_x_api_key()
    {
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);
        var credentialOverride = new RouteCredentialOverride("stored-secret", CredentialAuthScheme.ApiKey, BaseUrlOverride: null, AnthropicVersion: null, ForceBearer: false);

        await forwarder.SendAsync(Decision(ApiDialect.Anthropic), credentialOverride, ApiDialect.Anthropic, "{}", "/v1/messages", queryString: null, Ct);

        capture.ApiKey.ShouldBe("stored-secret");
        capture.Authorization.ShouldBeNull();
    }

    private static UpstreamForwarder Build(CapturingHandler handler) =>
        new(new StubHttpClientFactory(handler), NullLogger<UpstreamForwarder>.Instance);

    private static RouteDecision Decision(ApiDialect dialect) => new(
        new ProviderRoute("provider", dialect, new Uri("https://upstream.test"), ApiKey: "config-key", IsDefault: true, AnthropicVersion: null, Models: []),
        TargetModel: "model",
        CachingEnabled: false,
        IsImposter: false);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        public string? ApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.TryGetValues("Authorization", out IEnumerable<string>? auth) ? string.Join(",", auth) : null;
            ApiKey = request.Headers.TryGetValues("x-api-key", out IEnumerable<string>? key) ? string.Join(",", key) : null;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
