extern alias HostApp;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SmoothLlmImposter.Host.IntegrationTest;

public sealed class CredentialAppFixture : WebApplicationFactory<HostApp::Program>
{
    public const string AdminKey = "admin-secret";
    public const string OperatorKey = "operator-secret";
    public StubUpstreamHandler Upstream { get; } = new();

    private static readonly Dictionary<string, string?> Config = new()
    {
        ["Admin:ApiKey"] = AdminKey,
        ["Admin:OperatorApiKey"] = OperatorKey,

        ["Imposter:Providers:openai-official:Dialect"] = "openai",
        ["Imposter:Providers:openai-official:BaseUrl"] = "https://api.openai.test",
        ["Imposter:Providers:openai-official:Secret"] = "openai-config-key",
        ["Imposter:Providers:openai-official:IsDefault"] = "true",

        ["Imposter:Providers:opencode-go:Dialect"] = "openai",
        ["Imposter:Providers:opencode-go:BaseUrl"] = "https://opencode.test",
        ["Imposter:Providers:opencode-go:Secret"] = "opencode-key",
        ["Imposter:Providers:opencode-go:Models:0:From"] = "gpt5.4",
        ["Imposter:Providers:opencode-go:Models:0:To"] = "grok-code"
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
