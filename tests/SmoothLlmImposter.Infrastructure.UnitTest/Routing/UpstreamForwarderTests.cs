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

        await Send(forwarder, Decision(ApiDialect.Anthropic), credentialOverride, ApiDialect.Anthropic, CallerHeaders.None);

        capture.Authorization.ShouldBe("Bearer stored-secret");
        capture.ApiKey.ShouldBeNull();
    }

    [Fact]
    public async Task Without_force_bearer_an_apikey_scheme_credential_still_sends_x_api_key()
    {
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);
        var credentialOverride = new RouteCredentialOverride("stored-secret", CredentialAuthScheme.ApiKey, BaseUrlOverride: null, AnthropicVersion: null, ForceBearer: false);

        await Send(forwarder, Decision(ApiDialect.Anthropic), credentialOverride, ApiDialect.Anthropic, CallerHeaders.None);

        capture.ApiKey.ShouldBe("stored-secret");
        capture.Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task Keyless_passthrough_forwards_caller_authorization_bearer()
    {
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);
        CallerHeaders caller = Headers(("Authorization", "Bearer caller-key"));

        await Send(forwarder, Decision(ApiDialect.OpenAi, apiKey: null), credentialOverride: null, ApiDialect.OpenAi, caller);

        capture.Authorization.ShouldBe("Bearer caller-key");
        capture.ApiKey.ShouldBeNull();
    }

    [Fact]
    public async Task Keyless_passthrough_forwards_caller_x_api_key()
    {
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);
        CallerHeaders caller = Headers(("x-api-key", "caller-xkey"));

        await Send(forwarder, Decision(ApiDialect.Anthropic, apiKey: null), credentialOverride: null, ApiDialect.Anthropic, caller);

        capture.ApiKey.ShouldBe("caller-xkey");
        capture.Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task Caller_headers_are_forwarded_verbatim_on_passthrough()
    {
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);
        CallerHeaders caller = Headers(
            ("Authorization", "Bearer caller-key"),
            ("anthropic-beta", "context-management-2025-06-27"),
            ("anthropic-version", "2025-01-01"),
            ("x-stainless-lang", "js"));

        await Send(forwarder, Decision(ApiDialect.Anthropic, apiKey: null), credentialOverride: null, ApiDialect.Anthropic, caller);

        capture.Header("anthropic-beta").ShouldBe("context-management-2025-06-27");
        capture.Header("x-stainless-lang").ShouldBe("js");
        // The caller's own anthropic-version is preserved, not overridden by the default.
        capture.AnthropicVersion.ShouldBe("2025-01-01");
    }

    [Fact]
    public async Task Missing_anthropic_version_is_filled_with_the_default()
    {
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);
        CallerHeaders caller = Headers(("x-api-key", "caller-xkey"));

        await Send(forwarder, Decision(ApiDialect.Anthropic, apiKey: null), credentialOverride: null, ApiDialect.Anthropic, caller);

        capture.AnthropicVersion.ShouldBe("2023-06-01");
    }

    [Fact]
    public async Task Imposter_route_never_forwards_caller_authentication()
    {
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);
        CallerHeaders caller = Headers(("Authorization", "Bearer caller-key"), ("x-api-key", "caller-xkey"));

        await Send(forwarder, Decision(ApiDialect.OpenAi, apiKey: null, isImposter: true), credentialOverride: null, ApiDialect.OpenAi, caller);

        capture.Authorization.ShouldBeNull();
        capture.ApiKey.ShouldBeNull();
    }

    [Fact]
    public async Task Provider_key_takes_precedence_over_caller_authentication()
    {
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);
        CallerHeaders caller = Headers(("Authorization", "Bearer caller-key"));

        await Send(forwarder, Decision(ApiDialect.OpenAi, apiKey: "config-key"), credentialOverride: null, ApiDialect.OpenAi, caller);

        capture.Authorization.ShouldBe("Bearer config-key");
    }

    private static Task Send(
        UpstreamForwarder forwarder,
        RouteDecision decision,
        RouteCredentialOverride? credentialOverride,
        ApiDialect dialect,
        CallerHeaders caller) =>
        forwarder.SendAsync(decision, credentialOverride, dialect, "{}", dialect == ApiDialect.Anthropic ? "/v1/messages" : "/v1/chat/completions", queryString: null, caller, TestContext.Current.CancellationToken);

    private static CallerHeaders Headers(params (string Name, string Value)[] headers) =>
        new(headers.Select(h => new KeyValuePair<string, IReadOnlyList<string>>(h.Name, [h.Value])).ToArray());

    private static UpstreamForwarder Build(CapturingHandler handler) =>
        new(new StubHttpClientFactory(handler), NullLogger<UpstreamForwarder>.Instance);

    private static RouteDecision Decision(ApiDialect dialect, string? apiKey = "config-key", bool isImposter = false) => new(
        new ProviderRoute("provider", dialect, new Uri("https://upstream.test"), ApiKey: apiKey, IsDefault: !isImposter, AnthropicVersion: null, Models: []),
        TargetModel: "model",
        CachingEnabled: false,
        IsImposter: isImposter);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private HttpRequestMessage? _request;

        public string? Authorization => Header("Authorization");
        public string? ApiKey => Header("x-api-key");
        public string? AnthropicVersion => Header("anthropic-version");

        public string? Header(string name) =>
            _request is not null && _request.Headers.TryGetValues(name, out IEnumerable<string>? values)
                ? string.Join(",", values)
                : null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _request = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
