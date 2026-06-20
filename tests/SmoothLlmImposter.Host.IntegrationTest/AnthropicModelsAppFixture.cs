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
        ["Imposter:Providers:0:Name"] = "anthropic-official",
        ["Imposter:Providers:0:Dialect"] = "anthropic",
        ["Imposter:Providers:0:BaseUrl"] = "https://api.anthropic.test",
        ["Imposter:Providers:0:Secret"] = "anthropic-key",
        ["Imposter:Providers:0:IsDefault"] = "true",

        // Anthropic imposter A — two distinct targets.
        ["Imposter:Providers:1:Name"] = "anthropic-imposter-a",
        ["Imposter:Providers:1:Dialect"] = "anthropic",
        ["Imposter:Providers:1:BaseUrl"] = "https://imposter-a.test",
        ["Imposter:Providers:1:Secret"] = "a-secret-key",
        ["Imposter:Providers:1:Models:0:From"] = "alias-x",
        ["Imposter:Providers:1:Models:0:To"] = "claude-sonnet-4-6",
        ["Imposter:Providers:1:Models:1:From"] = "alias-y",
        ["Imposter:Providers:1:Models:1:To"] = "claude-opus-4-8",

        // Anthropic imposter B — re-declares claude-sonnet-4-6 (must dedup) + a new target.
        ["Imposter:Providers:2:Name"] = "anthropic-imposter-b",
        ["Imposter:Providers:2:Dialect"] = "anthropic",
        ["Imposter:Providers:2:BaseUrl"] = "https://imposter-b.test",
        ["Imposter:Providers:2:Secret"] = "b-secret-key",
        ["Imposter:Providers:2:Models:0:From"] = "alias-z",
        ["Imposter:Providers:2:Models:0:To"] = "claude-sonnet-4-6",
        ["Imposter:Providers:2:Models:1:From"] = "alias-w",
        ["Imposter:Providers:2:Models:1:To"] = "claude-haiku-4-5",

        // OpenAI imposter — its target must NOT appear in the Anthropic discovery list.
        ["Imposter:Providers:3:Name"] = "openai-imposter",
        ["Imposter:Providers:3:Dialect"] = "openai",
        ["Imposter:Providers:3:BaseUrl"] = "https://api.openai.test",
        ["Imposter:Providers:3:Secret"] = "openai-key",
        ["Imposter:Providers:3:IsDefault"] = "true",
        ["Imposter:Providers:3:Models:0:From"] = "gpt",
        ["Imposter:Providers:3:Models:0:To"] = "grok-code"
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
