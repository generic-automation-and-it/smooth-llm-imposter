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
        ["Imposter:Providers:0:Dialect"] = "openai",
        ["Imposter:Providers:0:BaseUrl"] = "https://api.openai.test",
        ["Imposter:Providers:0:ApiKey"] = "openai-key",
        ["Imposter:Providers:0:IsDefault"] = "true",

        ["Imposter:Providers:1:Name"] = "opencode-go",
        ["Imposter:Providers:1:Dialect"] = "openai",
        ["Imposter:Providers:1:BaseUrl"] = "https://opencode.test",
        ["Imposter:Providers:1:ApiKey"] = "opencode-key",
        ["Imposter:Providers:1:Models:0:From"] = "gpt5.4",
        ["Imposter:Providers:1:Models:0:To"] = "grok-code",
        ["Imposter:Providers:1:Models:0:Caching"] = "true",

        ["Imposter:Providers:2:Name"] = "anthropic-official",
        ["Imposter:Providers:2:Dialect"] = "anthropic",
        ["Imposter:Providers:2:BaseUrl"] = "https://api.anthropic.test",
        ["Imposter:Providers:2:ApiKey"] = "anthropic-key",
        ["Imposter:Providers:2:IsDefault"] = "true",
        ["Imposter:Providers:2:Models:0:From"] = "claude-haiku-*",
        ["Imposter:Providers:2:Models:0:To"] = "claude-3-5-haiku-latest",
        ["Imposter:Providers:2:Models:0:Caching"] = "true"
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Start from a clean slate: drop appsettings.json (and every other inherited source) so the
        // shipped providers cannot bleed in through key-by-key config merge. The in-memory collection
        // is then the single, authoritative routing config — the fixture starts with no imposters
        // except the ones declared in Config.
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
