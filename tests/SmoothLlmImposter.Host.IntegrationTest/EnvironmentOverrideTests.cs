extern alias HostApp;

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// Proves environment variables override lower-precedence configuration. The provider's empty
/// <c>Secret</c> — supplied by an in-memory layer that stands in for appsettings.json — is replaced by an
/// env-supplied secret, which the forwarder then sends upstream. Sources are layered in-memory (low) then
/// environment variables (high) so env wins. The fixture starts with no imposters beyond the single
/// matched provider declared here, so it does not depend on the shipped appsettings.json.
/// </summary>
public sealed class EnvironmentOverrideTests
{
    private static readonly Dictionary<string, string?> BaseConfig = new()
    {
        ["Imposter:Providers:0:Name"] = "opencode-go",
        ["Imposter:Providers:0:Dialect"] = "openai",
        ["Imposter:Providers:0:BaseUrl"] = "https://opencode.test",
        ["Imposter:Providers:0:Secret"] = "",
        ["Imposter:Providers:0:Models:0:From"] = "gpt5.4",
        ["Imposter:Providers:0:Models:0:To"] = "grok-code"
    };

    private sealed class EnvFixture : WebApplicationFactory<HostApp::Program>
    {
        public StubUpstreamHandler Upstream { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Clean slate, then layer sources so env wins: in-memory base (low) → env vars (high).
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.Sources.Clear();
                config.AddInMemoryCollection(BaseConfig);
                config.AddEnvironmentVariables();
            });

            builder.ConfigureServices(services =>
                services.AddHttpClient("imposter-upstream")
                    .ConfigurePrimaryHttpMessageHandler(() => Upstream));
        }
    }

    [Fact]
    public async Task Environment_variable_overrides_configured_secret()
    {
        const string envKey = "Imposter__Providers__0__Secret";
        string? original = Environment.GetEnvironmentVariable(envKey);
        Environment.SetEnvironmentVariable(envKey, "env-openai-key");

        try
        {
            using var factory = new EnvFixture();
            HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.PostAsync(
                "/v1/chat/completions",
                new StringContent("""{"model":"gpt5.4"}""", Encoding.UTF8, "application/json"),
                TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            factory.Upstream.LastAuthorization.ShouldBe("Bearer env-openai-key");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, original);
        }
    }
}
