extern alias HostApp;

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
/// Boots the Host with a multi-route OpenAI catalogue (a duplicate <c>to</c> across two providers, plus a
/// no-mapping default and an Anthropic provider) to exercise the HLD 005 <c>/openai/v1/models</c> aggregation:
/// distinct-union, first-declaring-owner dedup, OpenAI-only scope. Stub upstream so a test can assert the
/// discovery path makes no upstream call (NFR-03).
/// </summary>
public sealed class ModelsCatalogAppFixture : WebApplicationFactory<HostApp::Program>
{
    public StubUpstreamHandler Upstream { get; } = new();

    private static readonly Dictionary<string, string?> Config = new()
    {
        // Default OpenAI provider with no Models[] — contributes nothing to discovery.
        ["Imposter:Providers:0:Name"] = "openai-official",
        ["Imposter:Providers:0:Dialect"] = "openai",
        ["Imposter:Providers:0:BaseUrl"] = "https://api.openai.test",
        ["Imposter:Providers:0:Secret"] = "openai-key",
        ["Imposter:Providers:0:IsDefault"] = "true",

        ["Imposter:Providers:1:Name"] = "opencode-go",
        ["Imposter:Providers:1:Dialect"] = "openai",
        ["Imposter:Providers:1:BaseUrl"] = "https://opencode.test",
        ["Imposter:Providers:1:Secret"] = "opencode-key",
        ["Imposter:Providers:1:Models:0:From"] = "gpt5.4",
        ["Imposter:Providers:1:Models:0:To"] = "grok-code",
        ["Imposter:Providers:1:Models:1:From"] = "gpt-shared",
        ["Imposter:Providers:1:Models:1:To"] = "shared-model",

        ["Imposter:Providers:2:Name"] = "openrouter",
        ["Imposter:Providers:2:Dialect"] = "openai",
        ["Imposter:Providers:2:BaseUrl"] = "https://openrouter.test",
        ["Imposter:Providers:2:Secret"] = "openrouter-key",
        // Duplicate `to` ("shared-model") — must collapse to one entry owned by the first declarer (opencode-go).
        ["Imposter:Providers:2:Models:0:From"] = "gpt-shared-2",
        ["Imposter:Providers:2:Models:0:To"] = "shared-model",
        ["Imposter:Providers:2:Models:1:From"] = "gpt-z",
        ["Imposter:Providers:2:Models:1:To"] = "another-model",

        ["Imposter:Providers:3:Name"] = "anthropic-official",
        ["Imposter:Providers:3:Dialect"] = "anthropic",
        ["Imposter:Providers:3:BaseUrl"] = "https://api.anthropic.test",
        ["Imposter:Providers:3:Secret"] = "anthropic-key",
        ["Imposter:Providers:3:IsDefault"] = "true",
    };

    /// <summary>The distinct OpenAI <c>to</c> set in catalogue order — the expected discovery ids.</summary>
    public static readonly string[] ExpectedOpenAiModelIds = ["grok-code", "shared-model", "another-model"];

    /// <summary>Provider secrets that must never appear in the discovery response body (NFR-04).</summary>
    public static readonly string[] ConfiguredSecrets = ["openai-key", "opencode-key", "openrouter-key", "anthropic-key"];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.Sources.Clear();
            config.AddInMemoryCollection(Config);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICredentialStore>();
            services.AddSingleton<ICredentialStore, NoopCredentialStore>();
            services.AddHttpClient("imposter-upstream")
                .ConfigurePrimaryHttpMessageHandler(() => Upstream);
        });
    }

    private sealed class NoopCredentialStore : ICredentialStore
    {
        public Task<ProviderCredential> AddAsync(ProviderCredential credential, CancellationToken cancellationToken) => Task.FromResult(credential);

        public Task<IReadOnlyList<ProviderCredential>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProviderCredential>>(Array.Empty<ProviderCredential>());

        public Task<ProviderCredential?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<ProviderCredential?>(null);

        public Task<ProviderCredential?> GetActiveAsync(ApiDialect dialect, CancellationToken cancellationToken) => Task.FromResult<ProviderCredential?>(null);

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ProviderCredential> UpdateAsync(ProviderCredential credential, CancellationToken cancellationToken) => Task.FromResult(credential);

        public Task<ProviderCredential> ActivateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<ProviderCredential>(new OpenAiCredential("unused", "cipher", CredentialAuthScheme.Bearer, null));
    }
}
