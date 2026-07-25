extern alias HostApp;

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// End-to-end tests for HLD 010 (who-message introspection). Boots the real Host against the stub
/// upstream transport so the full pipeline runs — endpoint → router → responder → (short-circuit |
/// forwarder). Proves: (1) the probe short-circuits with no upstream call, (2) streaming requests
/// bypass the probe, and (3) disabling the feature forwards verbatim.
/// </summary>
public sealed class WhoMessageIntegrationTests
{
    private static Dictionary<string, string?> BuildConfig(bool whoMessageEnabled) => new()
    {
        // IConfiguration binder is case-insensitive for bool parsing; no need to force lowercase.
        ["Imposter:WhoMessage:Enabled"] = whoMessageEnabled.ToString(),

        ["Imposter:Providers:opencode-go:Dialect"] = "openai",
        ["Imposter:Providers:opencode-go:BaseUrl"] = "https://opencode.test",
        ["Imposter:Providers:opencode-go:Secret"] = "opencode-key",
        ["Imposter:Providers:opencode-go:AuthScheme"] = "ApiKey",
        ["Imposter:Providers:opencode-go:Models:0:From"] = "gpt5.4",
        ["Imposter:Providers:opencode-go:Models:0:To"] = "grok-code",

        ["Imposter:Providers:openai-official:Dialect"] = "openai",
        ["Imposter:Providers:openai-official:BaseUrl"] = "https://api.openai.test",
        ["Imposter:Providers:openai-official:Secret"] = "openai-key",
        ["Imposter:Providers:openai-official:IsDefault"] = "true",

        ["Imposter:Providers:anthropic-official:Dialect"] = "anthropic",
        ["Imposter:Providers:anthropic-official:BaseUrl"] = "https://api.anthropic.test",
        ["Imposter:Providers:anthropic-official:Secret"] = "anthropic-key",
        ["Imposter:Providers:anthropic-official:IsDefault"] = "true",
        ["Imposter:Providers:anthropic-official:Models:0:From"] = "claude-haiku-*",
        ["Imposter:Providers:anthropic-official:Models:0:To"] = "claude-3-5-haiku-latest",
    };

    private sealed class Fixture : WebApplicationFactory<HostApp::Program>
    {
        public StubUpstreamHandler Upstream { get; } = new();
        private readonly bool _whoMessageEnabled;

        public Fixture(bool whoMessageEnabled = true)
        {
            _whoMessageEnabled = whoMessageEnabled;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.Sources.Clear();
                config.AddInMemoryCollection(BuildConfig(_whoMessageEnabled));
            });

            builder.ConfigureServices(services =>
            {
                // Mirror ImposterAppFixture: drop the real InMemoryCredentialStore singleton so a future
                // passthrough-path test in this fixture does not silently hit the live store. The current
                // tests are safe (they all hit imposter routes), but this keeps the test seam uniform.
                services.RemoveAll<ICredentialStore>();
                services.AddSingleton<ICredentialStore, NoopCredentialStore>();
                services.AddHttpClient("imposter-upstream")
                    .ConfigurePrimaryHttpMessageHandler(() => Upstream);
            });
        }
    }

    /// <summary>No-op credential store: satisfies the <see cref="ICredentialStore"/> contract without
    /// returning any credential, mirroring the sibling in <c>ImposterAppFixture</c>.</summary>
    private sealed class NoopCredentialStore : ICredentialStore
    {
        public Task<ProviderCredential> AddAsync(ProviderCredential credential, CancellationToken cancellationToken) => Task.FromResult(credential);
        public Task<IReadOnlyList<ProviderCredential>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProviderCredential>>(Array.Empty<ProviderCredential>());
        public Task<ProviderCredential?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<ProviderCredential?>(null);
        public Task<ProviderCredential?> GetActiveAsync(ApiDialect dialect, string providerName, CancellationToken cancellationToken) => Task.FromResult<ProviderCredential?>(null);
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ProviderCredential> UpdateAsync(ProviderCredential credential, CancellationToken cancellationToken) => Task.FromResult(credential);
        public Task<ProviderCredential> ActivateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<ProviderCredential>(new OpenAiCredential("unused-provider", "unused", "cipher", CredentialAuthScheme.Bearer, null));
    }

    [Fact]
    public async Task Who_probe_short_circuits_with_no_upstream_call_and_dialect_shaped_envelope()
    {
        using var factory = new Fixture();
        HttpClient client = factory.CreateClient();
        int upstreamBefore = factory.Upstream.RequestCount;

        string body = """{"model":"gpt5.4","messages":[{"role":"user","content":"who?"}]}""";
        using HttpResponseMessage response = await client.PostAsync(
            "/openai/v1/chat/completions",
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        JsonObject root = JsonNode.Parse(responseBody)!.AsObject();

        // OpenAI chat-completion envelope with zero usage and the who- prefix on the synthetic id.
        root["object"]!.GetValue<string>().ShouldBe("chat.completion");
        root["id"]!.GetValue<string>().ShouldStartWith("chatcmpl-who-");
        JsonNode choice = root["choices"]!.AsArray().Single()!;
        choice["finish_reason"]!.GetValue<string>().ShouldBe("stop");
        string content = choice["message"]!["content"]!.GetValue<string>();
        content.ShouldBe("Imposter: gpt5.4 → grok-code (auth: ApiKey)");
        root["usage"]!["total_tokens"]!.GetValue<int>().ShouldBe(0);

        // The forwarder must NOT have been called — zero upstream cost on match (NFR-02).
        factory.Upstream.RequestCount.ShouldBe(upstreamBefore);
    }

    [Fact]
    public async Task Streaming_request_with_who_content_is_forwarded_to_upstream()
    {
        using var factory = new Fixture();
        HttpClient client = factory.CreateClient();
        int upstreamBefore = factory.Upstream.RequestCount;

        string body = """{"model":"gpt5.4","messages":[{"role":"user","content":"who?"}],"stream":true}""";
        using HttpResponseMessage response = await client.PostAsync(
            "/openai/v1/chat/completions",
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Streaming + who? content → the probe does NOT fire (LADR-05); the forwarder runs instead.
        factory.Upstream.RequestCount.ShouldBe(upstreamBefore + 1);
        factory.Upstream.LastRequestBody.ShouldNotBeNull();
        factory.Upstream.LastRequestBody.ShouldContain("\"stream\":true");
    }

    [Fact]
    public async Task Feature_disabled_forwards_who_content_verbatim_to_upstream()
    {
        using var factory = new Fixture(whoMessageEnabled: false);
        HttpClient client = factory.CreateClient();
        int upstreamBefore = factory.Upstream.RequestCount;

        string body = """{"model":"gpt5.4","messages":[{"role":"user","content":"who?"}]}""";
        using HttpResponseMessage response = await client.PostAsync(
            "/openai/v1/chat/completions",
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Disabled → the probe never runs; the upstream sees the body verbatim (NFR-01).
        factory.Upstream.RequestCount.ShouldBe(upstreamBefore + 1);
        factory.Upstream.LastRequestBody.ShouldNotBeNull();
        factory.Upstream.LastRequestBody.ShouldContain("who?");
    }

    [Fact]
    public async Task Non_trigger_content_forwards_to_upstream_with_body_intact()
    {
        using var factory = new Fixture();
        HttpClient client = factory.CreateClient();
        int upstreamBefore = factory.Upstream.RequestCount;

        string body = """{"model":"gpt5.4","messages":[{"role":"user","content":"hello, what can you do?"}]}""";
        using HttpResponseMessage response = await client.PostAsync(
            "/openai/v1/chat/completions",
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Upstream.RequestCount.ShouldBe(upstreamBefore + 1);
        factory.Upstream.LastRequestBody.ShouldNotBeNull();
        factory.Upstream.LastRequestBody.ShouldContain("hello, what can you do?");
    }

    [Fact]
    public async Task Anthropic_dialect_who_probe_returns_message_envelope()
    {
        using var factory = new Fixture();
        HttpClient client = factory.CreateClient();
        int upstreamBefore = factory.Upstream.RequestCount;

        // The claude-haiku-* mapping on anthropic-official rewrites to claude-3-5-haiku-latest. The dialect
        // default scheme is ApiKey (no explicit AuthScheme configured), so DescribeAuth returns "ApiKey".
        string body = """{"model":"claude-haiku-3","messages":[{"role":"user","content":"who?"}]}""";
        using HttpResponseMessage response = await client.PostAsync(
            "/anthropic/v1/messages",
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        JsonObject root = JsonNode.Parse(responseBody)!.AsObject();

        root["type"]!.GetValue<string>().ShouldBe("message");
        root["stop_reason"]!.GetValue<string>().ShouldBe("end_turn");
        root["model"]!.GetValue<string>().ShouldBe("claude-haiku-3");
        JsonNode textBlock = root["content"]!.AsArray().Single()!;
        textBlock["type"]!.GetValue<string>().ShouldBe("text");
        textBlock["text"]!.GetValue<string>().ShouldBe("Imposter: claude-haiku-3 → claude-3-5-haiku-latest (auth: ApiKey)");
        root["usage"]!["input_tokens"]!.GetValue<int>().ShouldBe(0);
        root["usage"]!["output_tokens"]!.GetValue<int>().ShouldBe(0);

        factory.Upstream.RequestCount.ShouldBe(upstreamBefore);
    }
}
