extern alias HostApp;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// End-to-end proof of the key-less catch-all passthrough: with no provider key, no stored override, and
/// no PostgreSQL configured (so the real <c>NullCredentialStore</c> is registered), the router relays the
/// caller's own inbound credential to the real provider. A matched imposter route still replaces it.
/// </summary>
public sealed class KeylessPassthroughIntegrationTests(KeylessPassthroughIntegrationTests.Fixture fixture)
    : IClassFixture<KeylessPassthroughIntegrationTests.Fixture>
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Keyless_passthrough_forwards_caller_x_api_key()
    {
        HttpClient client = fixture.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = Json("""{"model":"claude-opus-4-8","max_tokens":16,"messages":[{"role":"user","content":"hi"}]}""")
        };
        request.Headers.TryAddWithoutValidation("x-api-key", "caller-anthropic-key");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://api.anthropic.test/v1/messages");
        fixture.Upstream.LastApiKey.ShouldBe("caller-anthropic-key");
        fixture.Upstream.LastAnthropicVersion.ShouldBe("2023-06-01");
    }

    [Fact]
    public async Task Keyless_passthrough_forwards_caller_beta_and_vendor_headers_verbatim()
    {
        HttpClient client = fixture.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages?beta=true")
        {
            Content = Json("""{"model":"claude-opus-4-8","max_tokens":16,"context_management":{},"messages":[{"role":"user","content":"hi"}]}""")
        };
        request.Headers.TryAddWithoutValidation("x-api-key", "caller-anthropic-key");
        request.Headers.TryAddWithoutValidation("anthropic-beta", "context-management-2025-06-27");
        request.Headers.TryAddWithoutValidation("x-stainless-lang", "js");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastAnthropicBeta.ShouldBe("context-management-2025-06-27");
        fixture.Upstream.LastRequestBody!.ShouldContain("context_management");
    }

    [Fact]
    public async Task Keyless_passthrough_forwards_caller_bearer_authorization()
    {
        HttpClient client = fixture.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = Json("""{"model":"claude-opus-4-8","max_tokens":16,"messages":[{"role":"user","content":"hi"}]}""")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-bearer-token");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastAuthorization.ShouldBe("Bearer caller-bearer-token");
    }

    [Fact]
    public async Task Matched_imposter_route_replaces_caller_authentication_with_provider_key()
    {
        HttpClient client = fixture.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = Json("""{"model":"claude-haiku-4-5","max_tokens":16,"messages":[{"role":"user","content":"hi"}]}""")
        };
        request.Headers.TryAddWithoutValidation("x-api-key", "caller-anthropic-key");

        using HttpResponseMessage response = await client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://opencode.test/v1/messages");
        fixture.Upstream.LastApiKey.ShouldBe("opencode-key");
        fixture.Upstream.LastApiKey.ShouldNotBe("caller-anthropic-key");
    }

    public sealed class Fixture : WebApplicationFactory<HostApp::Program>
    {
        public StubUpstreamHandler Upstream { get; } = new();

        // No connection string ⇒ the real NullCredentialStore is registered (no store swap here on purpose).
        // Provider 0 is a key-less anthropic default (catch-all passthrough); provider 1 is a keyed imposter.
        private static readonly Dictionary<string, string?> Config = new()
        {
            ["Imposter:Providers:0:Name"] = "anthropic-default",
            ["Imposter:Providers:0:Dialect"] = "anthropic",
            ["Imposter:Providers:0:BaseUrl"] = "https://api.anthropic.test",
            ["Imposter:Providers:0:IsDefault"] = "true",

            ["Imposter:Providers:1:Name"] = "opencode-anthropic",
            ["Imposter:Providers:1:Dialect"] = "anthropic",
            ["Imposter:Providers:1:BaseUrl"] = "https://opencode.test",
            ["Imposter:Providers:1:ApiKey"] = "opencode-key",
            ["Imposter:Providers:1:Models:0:From"] = "claude-haiku-*",
            ["Imposter:Providers:1:Models:0:To"] = "minimax-m3"
        };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.Sources.Clear();
                config.AddInMemoryCollection(Config);
            });

            builder.ConfigureServices(services =>
                services.AddHttpClient("imposter-upstream")
                    .ConfigurePrimaryHttpMessageHandler(() => Upstream));
        }
    }
}
