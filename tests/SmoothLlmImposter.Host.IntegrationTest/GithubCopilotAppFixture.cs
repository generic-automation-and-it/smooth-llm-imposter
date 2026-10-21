extern alias HostApp;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.GithubCopilot;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Application.Features.GithubCopilot;
using SmoothLlmImposter.Domain.Credentials;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// Boots the real Host with the Copilot feature enabled against a temporary SQLite database and key ring,
/// substituting only the GitHub OAuth/Copilot clients and the upstream transport. Persistence, orchestration,
/// publication, routing and the endpoints stay real.
/// </summary>
public sealed class GithubCopilotAppFixture : WebApplicationFactory<HostApp::Program>
{
    public const string AdminKey = "test-admin-key";

    public GithubCopilotAppFixture(
        string? databasePath = null,
        bool enabled = true,
        bool seedReservedProviders = false,
        bool seedReservedMappings = false,
        bool deleteDataOnDispose = true,
        bool collideReservedName = false)
    {
        DeleteDataOnDispose = deleteDataOnDispose;
        OAuth = new FakeGithubOAuthClient(Clock);
        DataDirectory = databasePath is null
            ? Path.Combine(Path.GetTempPath(), $"copilot-l2-{Guid.NewGuid():N}")
            : Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        DatabasePath = databasePath ?? Path.Combine(DataDirectory, "copilot.db");
        Enabled = enabled;

        Dictionary<string, string?> config = new()
        {
            ["Admin:ApiKey"] = AdminKey,
            ["Imposter:Providers:ordinary:Dialect"] = "openai",
            ["Imposter:Providers:ordinary:BaseUrl"] = "https://ordinary.test",
            ["Imposter:Providers:ordinary:Secret"] = "ordinary-key",
            ["Imposter:Providers:ordinary:IsDefault"] = "true",
            ["GithubCopilot:DatabasePath"] = DatabasePath
        };

        if (collideReservedName)
        {
            // A registry that cannot accept the reserved key because its effective name is already taken: the
            // credential commits but publication is refused, exercising the D2 saved-not-published contract.
            config["Imposter:Providers:collision:Dialect"] = "openai";
            config["Imposter:Providers:collision:BaseUrl"] = "https://collision.test";
            config["Imposter:Providers:collision:Name"] = "company1-github-anthropic";
        }

        if (seedReservedProviders)
        {
            AddReservedProvider(config, "company1-github-anthropic", "anthropic", seedReservedMappings);
            AddReservedProvider(config, "company1-github-openai", "openai", seedReservedMappings);
        }

        Config = config;
    }

    public string DataDirectory { get; }

    public string DatabasePath { get; }

    public bool Enabled { get; }

    public bool DeleteDataOnDispose { get; }

    public IReadOnlyDictionary<string, string?> Config { get; }

    public ManualTimeProvider Clock { get; } = new();

    public FakeGithubOAuthClient OAuth { get; }

    public FakeGithubCopilotModelsClient Models { get; } = new();

    public StubUpstreamHandler Upstream { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.Sources.Clear();
            config.AddInMemoryCollection(Config);
        });

        builder.ConfigureTestServices(services =>
        {
            // The env-driven post-configure is authoritative in production; here it is replaced so the suite
            // never touches the process environment.
            services.RemoveAll<IPostConfigureOptions<GithubCopilotOptions>>();
            services.AddSingleton<IPostConfigureOptions<GithubCopilotOptions>>(
                new TestGithubCopilotOptionsPostConfigure(Enabled));

            services.RemoveAll<IGithubOAuthClient>();
            services.AddSingleton<IGithubOAuthClient>(OAuth);

            services.RemoveAll<IGithubCopilotModelsClient>();
            services.AddSingleton<IGithubCopilotModelsClient>(Models);

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            services.RemoveAll<ICredentialStore>();
            services.AddSingleton<ICredentialStore, NoopCredentialStore>();

            services.AddHttpClient("imposter-upstream")
                .ConfigurePrimaryHttpMessageHandler(() => Upstream);
            services.AddHttpClient("imposter-upstream-copilot")
                .ConfigurePrimaryHttpMessageHandler(() => Upstream);
        });
    }

    public HttpClient CreateAdminClient()
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Api-Key", AdminKey);
        return client;
    }

    public static async Task<GithubCopilotLoginStartResponse> StartLoginAsync(HttpClient client, string? prefix, string clientId)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/admin/github-copilot/logins",
            new { prefix, clientId });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<GithubCopilotLoginStartResponse>(TestContext.Current.CancellationToken))!;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && DeleteDataOnDispose && Directory.Exists(DataDirectory))
        {
            try
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // SQLite may still hold the file briefly on some platforms; the temp directory is disposable.
            }
        }
    }

    private static void AddReservedProvider(Dictionary<string, string?> config, string key, string dialect, bool withMappings)
    {
        config[$"Imposter:Providers:{key}:Dialect"] = dialect;
        config[$"Imposter:Providers:{key}:BaseUrl"] = "https://api.githubcopilot.com";
        config[$"Imposter:Providers:{key}:AuthScheme"] = "Bearer";
        config[$"Imposter:Providers:{key}:OpenAiUpstreamApi"] = "responses";
        config[$"Imposter:Providers:{key}:RequestNormalization"] = "none";
        if (withMappings)
        {
            config[$"Imposter:Providers:{key}:Models:0:From"] = dialect == "anthropic" ? "claude-*" : "gpt-*";
            config[$"Imposter:Providers:{key}:Models:0:To"] = dialect == "anthropic" ? "claude-haiku-4.5" : "gpt-5.3-codex";
        }
    }

    private sealed class TestGithubCopilotOptionsPostConfigure(bool enabled) : IPostConfigureOptions<GithubCopilotOptions>
    {
        public void PostConfigure(string? name, GithubCopilotOptions options)
        {
            options.Enabled = enabled;
        }
    }

    private sealed class NoopCredentialStore : ICredentialStore
    {
        public Task<ProviderCredential> AddAsync(ProviderCredential credential, CancellationToken cancellationToken) => Task.FromResult(credential);
        public Task<IReadOnlyList<ProviderCredential>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProviderCredential>>([]);
        public Task<ProviderCredential?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<ProviderCredential?>(null);
        public Task<ProviderCredential?> GetActiveAsync(ApiDialect dialect, string providerName, CancellationToken cancellationToken) => Task.FromResult<ProviderCredential?>(null);
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ProviderCredential> UpdateAsync(ProviderCredential credential, CancellationToken cancellationToken) => Task.FromResult(credential);
        public Task<ProviderCredential> ActivateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<ProviderCredential>(new OpenAiCredential("unused", "unused", "cipher", CredentialAuthScheme.Bearer, null));
    }
}

/// <summary>Controllable clock so polling intervals elapse without sleeps.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

public sealed class FakeGithubOAuthClient(TimeProvider timeProvider) : IGithubOAuthClient
{
    public Queue<GithubTokenPollResult> PollResults { get; } = new();

    public int PollCalls { get; private set; }

    public int RefreshCalls { get; private set; }

    public GithubUser? User { get; set; } = new("octocat", 1);

    public GithubCopilotEntitlement? Entitlement { get; set; } =
        new(ChatEnabled: true, ApiOrigin: "https://api.githubcopilot.com");

    public Task<GithubDeviceCode> StartDeviceFlowAsync(string clientId, CancellationToken cancellationToken) =>
        Task.FromResult(new GithubDeviceCode(
            "device-secret",
            "ABCD-1234",
            "https://github.com/login/device",
            timeProvider.GetUtcNow().AddMinutes(15),
            5));

    public Task<GithubTokenPollResult> PollTokenAsync(string clientId, string deviceCode, CancellationToken cancellationToken)
    {
        PollCalls++;
        return Task.FromResult(PollResults.Count > 0 ? PollResults.Dequeue() : new GithubTokenPollResult(GithubTokenPollKind.Pending));
    }

    public Task<GithubTokenPollResult> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken)
    {
        RefreshCalls++;
        return Task.FromResult(new GithubTokenPollResult(GithubTokenPollKind.Success, "refreshed-access", "refreshed-refresh"));
    }

    public Task<GithubUser?> GetUserAsync(string accessToken, CancellationToken cancellationToken) => Task.FromResult(User);

    public Task<GithubCopilotEntitlement?> GetCopilotEntitlementAsync(string accessToken, CancellationToken cancellationToken) =>
        Task.FromResult(Entitlement);
}

public sealed class FakeGithubCopilotModelsClient : IGithubCopilotModelsClient
{
    public GithubCopilotModel[] Models { get; set; } =
    [
        new("claude-haiku-4.5", ["messages"], null),
        new("gpt-5.3-codex", ["responses"], null),
        new("chat-only", ["chat"], null)
    ];

    public Task<GithubCopilotModel[]> ListModelsAsync(string accessToken, string apiOrigin, CancellationToken cancellationToken) =>
        Task.FromResult(Models);
}
