extern alias HostApp;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// Boots the real Host in-process and swaps the "imposter-upstream" client's transport for a stub, so
/// the genuine forwarder (URL building, auth headers, transformed body) runs against a capture handler.
/// Routing config is supplied in-memory so tests don't depend on appsettings.json contents.
/// </summary>
public sealed class ImposterAppFixture : WebApplicationFactory<HostApp::Program>
{
    public StubUpstreamHandler Upstream { get; } = new();

    private static readonly Dictionary<string, string?> Config = new()
    {
        ["Imposter:Providers:0:Name"] = "openai-official",
        ["Imposter:Providers:0:Api"] = "openai",
        ["Imposter:Providers:0:BaseUrl"] = "https://api.openai.test",
        ["Imposter:Providers:0:ApiKey"] = "openai-key",
        ["Imposter:Providers:0:IsDefault"] = "true",

        ["Imposter:Providers:1:Name"] = "opencode-go",
        ["Imposter:Providers:1:Api"] = "openai",
        ["Imposter:Providers:1:BaseUrl"] = "https://opencode.test",
        ["Imposter:Providers:1:ApiKey"] = "opencode-key",
        ["Imposter:Providers:1:Models:0:From"] = "gpt5.4",
        ["Imposter:Providers:1:Models:0:To"] = "grok-code",
        ["Imposter:Providers:1:Models:0:Caching"] = "true",

        ["Imposter:Providers:2:Name"] = "anthropic-official",
        ["Imposter:Providers:2:Api"] = "anthropic",
        ["Imposter:Providers:2:BaseUrl"] = "https://api.anthropic.test",
        ["Imposter:Providers:2:ApiKey"] = "anthropic-key",
        ["Imposter:Providers:2:IsDefault"] = "true",
        ["Imposter:Providers:2:Models:0:From"] = "claude-haiku-*",
        ["Imposter:Providers:2:Models:0:To"] = "claude-3-5-haiku-latest",
        ["Imposter:Providers:2:Models:0:Caching"] = "true"
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Config));

        builder.ConfigureServices(services =>
            services.AddHttpClient("imposter-upstream")
                .ConfigurePrimaryHttpMessageHandler(() => Upstream));
    }
}
