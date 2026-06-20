extern alias HostApp;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// Boots the Host with a multi-route Anthropic catalogue (plus a passthrough default and an OpenAI provider)
/// to exercise the locally-synthesized <c>GET /anthropic/v1/models</c> aggregation (HLD 005, Anthropic scope).
/// The Anthropic imposters declare overlapping <c>to</c> targets so dedup is observable; the OpenAI provider
/// must not leak into the Anthropic discovery list. The stub upstream is wired only to prove that serving the
/// endpoint issues NO upstream call (NFR-03) — no test in this fixture should ever reach it.
/// </summary>
public sealed class AnthropicModelsAppFixture : WebApplicationFactory<HostApp::Program>
{
    public StubUpstreamHandler Upstream { get; } = new();

    private static readonly Dictionary<string, string?> Config = new()
    {
        // Anthropic passthrough default — no Models[], so it contributes nothing to the discovery list.
        ["Imposter:Providers:anthropic-official:Dialect"] = "anthropic",
        ["Imposter:Providers:anthropic-official:BaseUrl"] = "https://api.anthropic.test",
        ["Imposter:Providers:anthropic-official:Secret"] = "anthropic-key",
        ["Imposter:Providers:anthropic-official:IsDefault"] = "true",

        // Anthropic imposter A — two distinct targets.
        ["Imposter:Providers:anthropic-imposter-a:Dialect"] = "anthropic",
        ["Imposter:Providers:anthropic-imposter-a:BaseUrl"] = "https://imposter-a.test",
        ["Imposter:Providers:anthropic-imposter-a:Secret"] = "a-secret-key",
        ["Imposter:Providers:anthropic-imposter-a:Models:0:From"] = "alias-x",
        ["Imposter:Providers:anthropic-imposter-a:Models:0:To"] = "claude-sonnet-4-6",
        ["Imposter:Providers:anthropic-imposter-a:Models:1:From"] = "alias-y",
        ["Imposter:Providers:anthropic-imposter-a:Models:1:To"] = "claude-opus-4-8",

        // Anthropic imposter B — re-declares claude-sonnet-4-6 (must dedup) + a new target.
        ["Imposter:Providers:anthropic-imposter-b:Dialect"] = "anthropic",
        ["Imposter:Providers:anthropic-imposter-b:BaseUrl"] = "https://imposter-b.test",
        ["Imposter:Providers:anthropic-imposter-b:Secret"] = "b-secret-key",
        ["Imposter:Providers:anthropic-imposter-b:Models:0:From"] = "alias-z",
        ["Imposter:Providers:anthropic-imposter-b:Models:0:To"] = "claude-sonnet-4-6",
        ["Imposter:Providers:anthropic-imposter-b:Models:1:From"] = "alias-w",
        ["Imposter:Providers:anthropic-imposter-b:Models:1:To"] = "claude-haiku-4-5",

        // OpenAI imposter — its target must NOT appear in the Anthropic discovery list.
        ["Imposter:Providers:openai-imposter:Dialect"] = "openai",
        ["Imposter:Providers:openai-imposter:BaseUrl"] = "https://api.openai.test",
        ["Imposter:Providers:openai-imposter:Secret"] = "openai-key",
        ["Imposter:Providers:openai-imposter:IsDefault"] = "true",
        ["Imposter:Providers:openai-imposter:Models:0:From"] = "gpt",
        ["Imposter:Providers:openai-imposter:Models:0:To"] = "grok-code"
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.Sources.Clear();
            config.AddInMemoryCollection(Config);
        });

        builder.ConfigureServices(services =>
        {
            services.AddHttpClient("imposter-upstream")
                .ConfigurePrimaryHttpMessageHandler(() => Upstream);
        });
    }
}
