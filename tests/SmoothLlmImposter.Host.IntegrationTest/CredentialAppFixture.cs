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

public sealed class CredentialAppFixture : WebApplicationFactory<HostApp::Program>
{
    public const string AdminKey = "admin-secret";
    public const string OperatorKey = "operator-secret";
    public StubUpstreamHandler Upstream { get; } = new();

    private static readonly Dictionary<string, string?> Config = new()
    {
        ["Admin:ApiKey"] = AdminKey,
        ["Admin:OperatorApiKey"] = OperatorKey,

        ["Imposter:Providers:0:Name"] = "openai-official",
        ["Imposter:Providers:0:Dialect"] = "openai",
        ["Imposter:Providers:0:BaseUrl"] = "https://api.openai.test",
        ["Imposter:Providers:0:Secret"] = "openai-config-key",
        ["Imposter:Providers:0:IsDefault"] = "true",

        ["Imposter:Providers:1:Name"] = "opencode-go",
        ["Imposter:Providers:1:Dialect"] = "openai",
        ["Imposter:Providers:1:BaseUrl"] = "https://opencode.test",
        ["Imposter:Providers:1:Secret"] = "opencode-key",
        ["Imposter:Providers:1:Models:0:From"] = "gpt5.4",
        ["Imposter:Providers:1:Models:0:To"] = "grok-code"
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
            services.RemoveAll<ICredentialStore>();
            services.AddSingleton<ICredentialStore, TestCredentialStore>();
            services.AddHttpClient("imposter-upstream")
                .ConfigurePrimaryHttpMessageHandler(() => Upstream);
        });
    }

    private sealed class TestCredentialStore : ICredentialStore
    {
        private readonly List<ProviderCredential> _credentials = [];

        public Task<ProviderCredential> AddAsync(ProviderCredential credential, CancellationToken cancellationToken)
        {
            _credentials.Add(credential);
            return Task.FromResult(credential);
        }

        public Task<IReadOnlyList<ProviderCredential>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderCredential>>(_credentials.ToArray());

        public Task<ProviderCredential?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_credentials.FirstOrDefault(x => x.Id == id));

        public Task<ProviderCredential?> GetActiveAsync(ApiDialect dialect, CancellationToken cancellationToken) =>
            Task.FromResult(_credentials.FirstOrDefault(x => x.ProviderDialect == ToToken(dialect) && x.IsActive));

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            _credentials.RemoveAll(x => x.Id == id);
            return Task.CompletedTask;
        }

        public Task<ProviderCredential> UpdateAsync(ProviderCredential credential, CancellationToken cancellationToken) => Task.FromResult(credential);

        public Task<ProviderCredential> ActivateAsync(Guid id, CancellationToken cancellationToken)
        {
            ProviderCredential credential = _credentials.Single(x => x.Id == id);
            foreach (ProviderCredential sibling in _credentials.Where(x => x.ProviderDialect == credential.ProviderDialect))
            {
                if (sibling.Id == id)
                {
                    sibling.Activate();
                }
                else
                {
                    sibling.Deactivate();
                }
            }

            return Task.FromResult(credential);
        }

        private static string ToToken(ApiDialect dialect) => dialect switch
        {
            ApiDialect.OpenAi => OpenAiCredential.DialectToken,
            ApiDialect.Anthropic => AnthropicCredential.DialectToken,
            _ => throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "Unsupported provider dialect.")
        };
    }
}
