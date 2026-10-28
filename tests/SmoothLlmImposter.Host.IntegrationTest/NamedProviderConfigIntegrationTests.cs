extern alias HostApp;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// L2 coverage for HLD 007 startup behaviour: the Host fails fast on the legacy array shape (NFR-02), and
/// resolving the named-dictionary config + conventional env surface opens no DB connection and issues no
/// upstream request at startup (NFR-04). Both boot the real Host via <see cref="WebApplicationFactory{T}"/>
/// with a clean in-memory config so they don't depend on appsettings.json.
/// </summary>
public sealed class NamedProviderConfigIntegrationTests
{
    [Fact]
    public void Host_fails_to_start_on_legacy_array_shape()
    {
        // Index keys are exactly how a legacy JSON array binds into the dictionary ("0","1",…). The
        // numeric-key guard must reject this at ValidateOnStart, so the host refuses to start.
        var legacyArrayConfig = new Dictionary<string, string?>
        {
            ["Imposter:Providers:0:Dialect"] = "openai",
            ["Imposter:Providers:0:BaseUrl"] = "https://api.openai.test",
            ["Imposter:Providers:0:IsDefault"] = "true",
        };

        using var factory = new ConfigFixture(legacyArrayConfig);

        // ValidateOnStart surfaces as OptionsValidationException when the host (and its IStartupValidator)
        // starts — CreateClient triggers that startup.
        var exception = Should.Throw<OptionsValidationException>(() => factory.CreateClient());
        exception.Failures.ShouldContain(f => f.Contains("name-keyed object"));
    }

    [Fact]
    public async Task Startup_with_named_config_and_conventional_env_makes_no_upstream_call()
    {
        const string envKey = "OPENCODE_GO_API_KEY";
        string? original = Environment.GetEnvironmentVariable(envKey);
        Environment.SetEnvironmentVariable(envKey, "conventional-key");

        var namedConfig = new Dictionary<string, string?>
        {
            ["Imposter:Providers:opencode-go:Dialect"] = "openai",
            ["Imposter:Providers:opencode-go:BaseUrl"] = "https://opencode.test",
            ["Imposter:Providers:opencode-go:Secret"] = "",
            ["Imposter:Providers:opencode-go:Models:0:From"] = "gpt5.4",
            ["Imposter:Providers:opencode-go:Models:0:To"] = "grok-code",
        };

        try
        {
            using var factory = new ConfigFixture(namedConfig, includeEnvironmentVariables: true);

            // Build/start the host (resolves + validates options, runs the post-configure resolver).
            HttpClient client = factory.CreateClient();

            // NFR-04: resolution is config/env only — nothing was forwarded upstream at startup.
            factory.Upstream.RequestCount.ShouldBe(0);

            // And the conventional secret took effect: the first matched request authenticates with it.
            using HttpResponseMessage response = await client.PostAsync(
                "/v1/chat/completions",
                new StringContent("""{"model":"gpt5.4"}""", System.Text.Encoding.UTF8, "application/json"),
                TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            factory.Upstream.RequestCount.ShouldBe(1);
            factory.Upstream.LastAuthorization.ShouldBe("Bearer conventional-key");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, original);
        }
    }

    [Fact]
    public void Host_fails_to_start_when_sticky_selection_has_no_tool_ordering()
    {
        // HLD 014 LADR-13: sticky selection keeps the forwarded tool set but not its order, so the
        // combination without first-seen ordering must refuse to boot and name both settings.
        var config = new Dictionary<string, string?>
        {
            ["Imposter:Providers:a:Dialect"] = "openai",
            ["Imposter:Providers:a:BaseUrl"] = "https://a.test",
            ["Imposter:Providers:a:IsDefault"] = "true",
            ["Imposter:DecisionsTools:Enabled"] = "true",
            ["Imposter:DecisionsTools:StickyPerSession"] = "true",
        };

        using var factory = new ConfigFixture(config);

        var exception = Should.Throw<OptionsValidationException>(() => factory.CreateClient());
        exception.Failures.ShouldContain(f => f.Contains("Imposter__ToolOrdering__Enabled"));
        exception.Failures.ShouldContain(f => f.Contains("DECISIONS_TOOLS_STICKY_PER_SESSION"));
    }

    [Fact]
    public void Host_fails_to_start_when_conventional_sticky_key_has_no_tool_ordering()
    {
        // The rule reads the post-configured options, so a conventional env value must trip it too.
        const string envKey = "DECISIONS_TOOLS_STICKY_PER_SESSION";
        string? original = Environment.GetEnvironmentVariable(envKey);
        Environment.SetEnvironmentVariable(envKey, "true");

        var config = new Dictionary<string, string?>
        {
            ["Imposter:Providers:a:Dialect"] = "openai",
            ["Imposter:Providers:a:BaseUrl"] = "https://a.test",
            ["Imposter:Providers:a:IsDefault"] = "true",
            ["Imposter:DecisionsTools:Enabled"] = "true",
        };

        try
        {
            using var factory = new ConfigFixture(config, includeEnvironmentVariables: true);

            var exception = Should.Throw<OptionsValidationException>(() => factory.CreateClient());
            exception.Failures.ShouldContain(f => f.Contains("DECISIONS_TOOLS_STICKY_PER_SESSION"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, original);
        }
    }

    [Fact]
    public void Host_starts_when_sticky_selection_has_tool_ordering()
    {
        var config = new Dictionary<string, string?>
        {
            ["Imposter:Providers:a:Dialect"] = "openai",
            ["Imposter:Providers:a:BaseUrl"] = "https://a.test",
            ["Imposter:Providers:a:IsDefault"] = "true",
            ["Imposter:DecisionsTools:Enabled"] = "true",
            ["Imposter:DecisionsTools:StickyPerSession"] = "true",
            ["Imposter:ToolOrdering:Enabled"] = "true",
        };

        using var factory = new ConfigFixture(config);

        using HttpClient client = factory.CreateClient();

        client.BaseAddress.ShouldNotBeNull();
        factory.Upstream.RequestCount.ShouldBe(0);
    }

    private sealed class ConfigFixture(
        Dictionary<string, string?> config,
        bool includeEnvironmentVariables = false) : WebApplicationFactory<HostApp::Program>
    {
        public StubUpstreamHandler Upstream { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                // Clean slate (no appsettings.json). Env vars are opt-in for tests that exercise that surface.
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(config);
                if (includeEnvironmentVariables)
                {
                    configuration.AddEnvironmentVariables();
                }
            });

            builder.ConfigureServices(services =>
                services.AddHttpClient("imposter-upstream")
                    .ConfigurePrimaryHttpMessageHandler(() => Upstream));
        }
    }
}
