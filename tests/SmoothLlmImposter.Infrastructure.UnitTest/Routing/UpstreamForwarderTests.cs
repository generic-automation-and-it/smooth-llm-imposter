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

        await Send(forwarder, Decision(ApiDialect.OpenAi, secret: null), credentialOverride: null, ApiDialect.OpenAi, caller);

        capture.Authorization.ShouldBe("Bearer caller-key");
        capture.ApiKey.ShouldBeNull();
    }

    [Fact]
    public async Task Keyless_passthrough_forwards_caller_x_api_key()
    {
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);
        CallerHeaders caller = Headers(("x-api-key", "caller-xkey"));

        await Send(forwarder, Decision(ApiDialect.Anthropic, secret: null), credentialOverride: null, ApiDialect.Anthropic, caller);

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

        await Send(forwarder, Decision(ApiDialect.Anthropic, secret: null), credentialOverride: null, ApiDialect.Anthropic, caller);

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

        await Send(forwarder, Decision(ApiDialect.Anthropic, secret: null), credentialOverride: null, ApiDialect.Anthropic, caller);

        capture.AnthropicVersion.ShouldBe("2023-06-01");
    }

    [Fact]
    public async Task Imposter_route_never_forwards_caller_authentication()
    {
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);
        CallerHeaders caller = Headers(("Authorization", "Bearer caller-key"), ("x-api-key", "caller-xkey"));

        await Send(forwarder, Decision(ApiDialect.OpenAi, secret: null, isImposter: true), credentialOverride: null, ApiDialect.OpenAi, caller);

        capture.Authorization.ShouldBeNull();
        capture.ApiKey.ShouldBeNull();
    }

    [Fact]
    public async Task Provider_key_takes_precedence_over_caller_authentication()
    {
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);
        CallerHeaders caller = Headers(("Authorization", "Bearer caller-key"));

        await Send(forwarder, Decision(ApiDialect.OpenAi, secret: "config-key"), credentialOverride: null, ApiDialect.OpenAi, caller);

        capture.Authorization.ShouldBe("Bearer config-key");
    }

    [Fact]
    public async Task Openai_provider_with_apikey_scheme_sends_only_x_api_key()
    {
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);

        await Send(
            forwarder,
            Decision(ApiDialect.OpenAi, secret: "opencode-key", authScheme: CredentialAuthScheme.ApiKey),
            credentialOverride: null,
            ApiDialect.OpenAi,
            CallerHeaders.None);

        capture.ApiKey.ShouldBe("opencode-key");
        capture.Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task Openai_provider_with_bearer_scheme_sends_only_authorization_bearer()
    {
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);

        await Send(
            forwarder,
            Decision(ApiDialect.OpenAi, secret: "router-key", authScheme: CredentialAuthScheme.Bearer),
            credentialOverride: null,
            ApiDialect.OpenAi,
            CallerHeaders.None);

        capture.Authorization.ShouldBe("Bearer router-key");
        capture.ApiKey.ShouldBeNull();
    }

    [Fact]
    public async Task Imposter_route_with_bearer_scheme_sends_provider_secret_as_authorization_bearer()
    {
        // Exactly the opencode-go scenario: a matched imposter route (IsImposter: true), Bearer scheme, with a
        // configured sk-prefixed Secret. The IsImposter flag is irrelevant once a secret is present — the secret
        // is applied before the caller-passthrough branch — so the wire header is "Bearer <secret>" verbatim.
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);
        CallerHeaders caller = Headers(("Authorization", "Bearer codex-caller-key"), ("openai-beta", "responses=v1"));

        await Send(
            forwarder,
            Decision(ApiDialect.OpenAi, secret: "sk-CMDWVUa", isImposter: true, authScheme: CredentialAuthScheme.Bearer),
            credentialOverride: null,
            ApiDialect.OpenAi,
            caller);

        capture.Authorization.ShouldBe("Bearer sk-CMDWVUa");
        capture.ApiKey.ShouldBeNull();
    }

    [Fact]
    public async Task Managed_secret_strips_caller_chatgpt_account_id()
    {
        // Codex relays chatgpt-account-id alongside its own Bearer; opencode honours that identity header over
        // the managed key and 401s. With a provider secret applied, the conflicting header must not reach upstream.
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);
        CallerHeaders caller = Headers(
            ("chatgpt-account-id", "22ecf56c-9a75-4e81-bd81-a85661953773"),
            ("originator", "codex_sdk_ts"));

        await Send(
            forwarder,
            Decision(ApiDialect.OpenAi, secret: "sk-CMDWVUa", isImposter: true, authScheme: CredentialAuthScheme.Bearer),
            credentialOverride: null,
            ApiDialect.OpenAi,
            caller);

        capture.Authorization.ShouldBe("Bearer sk-CMDWVUa");
        capture.Header("chatgpt-account-id").ShouldBeNull();
        capture.Header("originator").ShouldBe("codex_sdk_ts"); // non-identity telemetry still proxied verbatim
    }

    [Fact]
    public async Task Passthrough_keeps_caller_chatgpt_account_id()
    {
        // Key-less passthrough to the real backend: the caller's own credential + identity are a matched pair,
        // so chatgpt-account-id is relayed unchanged (no managed secret replaces the identity here).
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);
        CallerHeaders caller = Headers(
            ("Authorization", "Bearer caller-key"),
            ("chatgpt-account-id", "22ecf56c-9a75-4e81-bd81-a85661953773"));

        await Send(
            forwarder,
            Decision(ApiDialect.OpenAi, secret: null, isImposter: false),
            credentialOverride: null,
            ApiDialect.OpenAi,
            caller);

        capture.Authorization.ShouldBe("Bearer caller-key");
        capture.Header("chatgpt-account-id").ShouldBe("22ecf56c-9a75-4e81-bd81-a85661953773");
    }

    [Fact]
    public async Task Apikey_scheme_with_auth_header_override_writes_raw_token_to_custom_header()
    {
        // The LEGO codex gateway scenario: ApiKey scheme relocated to the `api-key` header. The value is the
        // raw token (no scheme prefix), and neither the default x-api-key nor Authorization is written.
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);

        await Send(
            forwarder,
            Decision(ApiDialect.OpenAi, secret: "lego-key", isImposter: true, authScheme: CredentialAuthScheme.ApiKey, authHeader: "api-key"),
            credentialOverride: null,
            ApiDialect.OpenAi,
            CallerHeaders.None);

        capture.Header("api-key").ShouldBe("lego-key");
        capture.ApiKey.ShouldBeNull();          // default x-api-key not used
        capture.Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task Bearer_scheme_with_auth_header_override_keeps_bearer_prefix_in_custom_header()
    {
        // AuthHeader relocates only the header name; the value format still follows the scheme, so a Bearer
        // credential in a custom header is `api-key: Bearer <token>`.
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);

        await Send(
            forwarder,
            Decision(ApiDialect.OpenAi, secret: "lego-token", isImposter: true, authScheme: CredentialAuthScheme.Bearer, authHeader: "api-key"),
            credentialOverride: null,
            ApiDialect.OpenAi,
            CallerHeaders.None);

        capture.Header("api-key").ShouldBe("Bearer lego-token");
        capture.Authorization.ShouldBeNull();   // default Authorization not used
    }

    [Fact]
    public async Task Bearer_scheme_does_not_double_prefix_a_secret_that_already_starts_with_bearer()
    {
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);

        await Send(
            forwarder,
            Decision(ApiDialect.OpenAi, secret: "Bearer already-prefixed", authScheme: CredentialAuthScheme.Bearer),
            credentialOverride: null,
            ApiDialect.OpenAi,
            CallerHeaders.None);

        capture.Authorization.ShouldBe("Bearer already-prefixed");
    }

    [Fact]
    public async Task Custom_auth_header_overrides_a_caller_relayed_header_of_the_same_name()
    {
        // A caller-supplied header sharing the AuthHeader name (not one of the withheld default auth headers)
        // is relayed by ForwardCallerHeaders; the managed credential must be the sole value upstream.
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);
        CallerHeaders caller = Headers(("api-key", "caller-supplied"));

        await Send(
            forwarder,
            Decision(ApiDialect.OpenAi, secret: "lego-key", isImposter: true, authScheme: CredentialAuthScheme.ApiKey, authHeader: "api-key"),
            credentialOverride: null,
            ApiDialect.OpenAi,
            caller);

        capture.Header("api-key").ShouldBe("lego-key");
    }

    [Fact]
    public async Task Anthropic_provider_without_scheme_defaults_to_x_api_key()
    {
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);

        await Send(
            forwarder,
            Decision(ApiDialect.Anthropic, secret: "anthropic-key", authScheme: null),
            credentialOverride: null,
            ApiDialect.Anthropic,
            CallerHeaders.None);

        capture.ApiKey.ShouldBe("anthropic-key");
        capture.Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task Body_less_get_forwards_the_get_method_with_no_content()
    {
        var capture = new CapturingHandler();
        UpstreamForwarder forwarder = Build(capture);

        await forwarder.SendAsync(
            Decision(ApiDialect.OpenAi),
            credentialOverride: null,
            ApiDialect.OpenAi,
            HttpMethod.Get,
            body: null,
            "/v1/models",
            queryString: null,
            CallerHeaders.None,
            Ct);

        capture.Method.ShouldBe(HttpMethod.Get);
        capture.HadContent.ShouldBeFalse();
    }

    private static Task Send(
        UpstreamForwarder forwarder,
        RouteDecision decision,
        RouteCredentialOverride? credentialOverride,
        ApiDialect dialect,
        CallerHeaders caller) =>
        forwarder.SendAsync(decision, credentialOverride, dialect, HttpMethod.Post, "{}", dialect == ApiDialect.Anthropic ? "/v1/messages" : "/v1/chat/completions", queryString: null, caller, TestContext.Current.CancellationToken);

    private static CallerHeaders Headers(params (string Name, string Value)[] headers) =>
        new(headers.Select(h => new KeyValuePair<string, IReadOnlyList<string>>(h.Name, [h.Value])).ToArray());

    private static UpstreamForwarder Build(CapturingHandler handler) =>
        new(new StubHttpClientFactory(handler), NullLogger<UpstreamForwarder>.Instance);

    private static RouteDecision Decision(
        ApiDialect dialect,
        string? secret = "config-key",
        bool isImposter = false,
        CredentialAuthScheme? authScheme = null,
        string? authHeader = null) => new(
        new ProviderRoute("provider", dialect, new Uri("https://upstream.test"), Secret: secret, IsDefault: !isImposter, AnthropicVersion: null, Models: [], AuthScheme: authScheme, AuthHeader: authHeader),
        TargetModel: "model",
        CachingEnabled: false,
        IsImposter: isImposter);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private HttpRequestMessage? _request;

        public string? Authorization => Header("Authorization");
        public string? ApiKey => Header("x-api-key");
        public string? AnthropicVersion => Header("anthropic-version");
        public HttpMethod? Method { get; private set; }
        public bool HadContent { get; private set; }

        public string? Header(string name) =>
            _request is not null && _request.Headers.TryGetValues(name, out IEnumerable<string>? values)
                ? string.Join(",", values)
                : null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _request = request;
            Method = request.Method;
            HadContent = request.Content is not null;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
