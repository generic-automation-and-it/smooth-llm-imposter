extern alias HostApp;

using System.Linq;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// Proves the <c>AuthHeader</c> override relocates the credential to a non-standard header end-to-end —
/// the MyCompany Gateway scenario, where the credential must be sent in a header literally named
/// <c>api-key</c> rather than the ApiKey scheme's default <c>x-api-key</c>. The value format still follows
/// the scheme (raw token for ApiKey), and the default auth headers are not written.
/// </summary>
public sealed class AuthHeaderOverrideIntegrationTests
{
    private static readonly Dictionary<string, string?> Config = new()
    {
        ["Imposter:Providers:mycompany-openai:Dialect"] = "openai",
        ["Imposter:Providers:mycompany-openai:BaseUrl"] = "https://mycompany.test/openai",
        ["Imposter:Providers:mycompany-openai:Secret"] = "mycompany-secret",
        ["Imposter:Providers:mycompany-openai:AuthScheme"] = "ApiKey",
        ["Imposter:Providers:mycompany-openai:AuthHeader"] = "api-key",
        ["Imposter:Providers:mycompany-openai:OpenAiUpstreamApi"] = "chat_completions",
        ["Imposter:Providers:mycompany-openai:Models:0:From"] = "gpt5.4",
        ["Imposter:Providers:mycompany-openai:Models:0:To"] = "gpt-5.4-2026-03-05"
    };

    private sealed class Fixture : WebApplicationFactory<HostApp::Program>
    {
        public StubUpstreamHandler Upstream { get; } = new();

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

    [Fact]
    public async Task Auth_header_override_sends_the_secret_in_the_custom_header_only()
    {
        using var factory = new Fixture();
        HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent("""{"model":"gpt5.4"}""", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Upstream.LastHeaders.TryGetValue("api-key", out string? apiKeyHeader).ShouldBeTrue();
        apiKeyHeader.ShouldBe("mycompany-secret");            // raw token (ApiKey value format), relocated header
        factory.Upstream.LastApiKey.ShouldBeNull();      // default x-api-key not used
        factory.Upstream.LastAuthorization.ShouldBeNull();
    }
}
