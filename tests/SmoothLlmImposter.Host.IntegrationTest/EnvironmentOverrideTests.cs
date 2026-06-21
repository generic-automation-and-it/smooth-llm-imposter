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
        ["Imposter:Providers:opencode-go:Dialect"] = "openai",
        ["Imposter:Providers:opencode-go:BaseUrl"] = "https://opencode.test",
        ["Imposter:Providers:opencode-go:Secret"] = "",
        ["Imposter:Providers:opencode-go:Models:0:From"] = "gpt5.4",
        ["Imposter:Providers:opencode-go:Models:0:To"] = "grok-code"
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
    public async Task Structured_environment_variable_overrides_configured_secret()
    {
        // Name-addressed structured override (HLD 007) — survives any provider reordering.
        await AssertSecretAppliedAsync("Imposter__Providers__opencode-go__Secret", "env-openai-key");
    }

    [Fact]
    public async Task Conventional_environment_variable_overrides_configured_secret()
    {
        // The conventional <NAME>_API_KEY surface (HLD 007) is the friendly path operators expect.
        await AssertSecretAppliedAsync("OPENCODE_GO_API_KEY", "conventional-openai-key");
    }

    // Every env var that can supply the opencode-go secret — the structured path and the conventional
    // surface. The test neutralizes all of them, then sets only the one under test, so an ambient
    // OPENCODE_GO_API_KEY in the developer's shell cannot pollute the assertion (the conventional var
    // legitimately wins over the structured one — exactly what this isolation proves out of band).
    private static readonly string[] OpencodeGoSecretVars =
        ["Imposter__Providers__opencode-go__Secret", "OPENCODE_GO_API_KEY"];

    private static async Task AssertSecretAppliedAsync(string envKey, string secret)
    {
        var originals = OpencodeGoSecretVars.ToDictionary(
            v => v, Environment.GetEnvironmentVariable);

        foreach (string v in OpencodeGoSecretVars)
        {
            Environment.SetEnvironmentVariable(v, null);
        }

        Environment.SetEnvironmentVariable(envKey, secret);

        try
        {
            using var factory = new EnvFixture();
            HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.PostAsync(
                "/v1/chat/completions",
                new StringContent("""{"model":"gpt5.4"}""", Encoding.UTF8, "application/json"),
                TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            factory.Upstream.LastAuthorization.ShouldBe($"Bearer {secret}");
        }
        finally
        {
            foreach ((string v, string? original) in originals)
            {
                Environment.SetEnvironmentVariable(v, original);
            }
        }
    }
}
