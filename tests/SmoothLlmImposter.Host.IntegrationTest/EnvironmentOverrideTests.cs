extern alias HostApp;

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// Proves environment variables override appsettings.json (the empty <c>ApiKey</c> for the default
/// openai provider is replaced by an env-supplied key, which the forwarder then sends upstream).
/// Uses appsettings.json directly (no in-memory config) so env-vs-file precedence is the thing tested.
/// </summary>
public sealed class EnvironmentOverrideTests
{
    private sealed class EnvFixture : WebApplicationFactory<HostApp::Program>
    {
        public StubUpstreamHandler Upstream { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureServices(services =>
                services.AddHttpClient("imposter-upstream")
                    .ConfigurePrimaryHttpMessageHandler(() => Upstream));
    }

    [Fact]
    public async Task Environment_variable_overrides_appsettings_api_key()
    {
        const string envKey = "Imposter__Providers__0__ApiKey";
        string? original = Environment.GetEnvironmentVariable(envKey);
        Environment.SetEnvironmentVariable(envKey, "env-openai-key");

        try
        {
            using var factory = new EnvFixture();
            HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.PostAsync(
                "/v1/chat/completions",
                new StringContent("""{"model":"gpt5.5"}""", Encoding.UTF8, "application/json"),
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
