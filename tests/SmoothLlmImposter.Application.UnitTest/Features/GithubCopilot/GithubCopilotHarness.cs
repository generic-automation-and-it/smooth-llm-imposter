using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.GithubCopilot;
using SmoothLlmImposter.Application.Common.Persistence;
using SmoothLlmImposter.Application.Features.GithubCopilot;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Features.GithubCopilot;

/// <summary>
/// Deterministic scaffolding for the Copilot workflow unit tests: a manual clock, an in-memory durable store,
/// a controllable OAuth client and the real publisher/coordinator wired exactly as the host wires them.
/// </summary>
internal sealed class GithubCopilotHarness
{
    public GithubCopilotHarness(bool enabled = true, string? clientIdFallback = "fallback-client")
    {
        Options = new GithubCopilotOptions
        {
            Enabled = enabled,
            DatabasePath = "/tmp/copilot-test.db",
            ClientId = clientIdFallback
        };

        Protector = new PrefixProtector();
        Publisher = new GithubCopilotPublisher(
            Registry,
            Connections,
            Protector,
            NullLogger<GithubCopilotPublisher>.Instance);

        Coordinator = new GithubCopilotLoginCoordinator(
            OAuth,
            Store,
            Protector,
            Publisher,
            Connections,
            Gate,
            Microsoft.Extensions.Options.Options.Create(Options),
            Time,
            NullLogger<GithubCopilotLoginCoordinator>.Instance);
    }

    public ManualTimeProvider Time { get; } = new();

    public StubConnectionStore Store { get; } = new();

    public InMemoryProviderRegistry Registry { get; } = new();

    public InMemoryGithubCopilotConnectionRegistry Connections { get; } = new();

    public GithubCopilotPrefixGate Gate { get; } = new();

    public FakeOAuthClient OAuth { get; } = new();

    public PrefixProtector Protector { get; }

    public GithubCopilotOptions Options { get; }

    public GithubCopilotPublisher Publisher { get; }

    public GithubCopilotLoginCoordinator Coordinator { get; }

    public GithubCopilotConnectionStatusReader StatusReader() =>
        new(Registry, Connections, Store, Microsoft.Extensions.Options.Options.Create(Options), Time);

    public GithubCopilotStartupCoordinator Startup() =>
        new(Store, Publisher, Microsoft.Extensions.Options.Options.Create(Options), NullLogger<GithubCopilotStartupCoordinator>.Instance);
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

internal sealed class PrefixProtector : IGithubCopilotSecretProtector
{
    private const string Prefix = "enc:";

    public string Protect(string plaintext) => Prefix + plaintext;

    public string Unprotect(string ciphertext) =>
        ciphertext.StartsWith(Prefix, StringComparison.Ordinal)
            ? ciphertext[Prefix.Length..]
            : throw new InvalidOperationException("ciphertext is not protected by this protector");
}

internal sealed class StubConnectionStore : IGithubCopilotConnectionStore
{
    private readonly Dictionary<string, GithubCopilotConnection> _rows = new(StringComparer.Ordinal);

    public bool FailOnUpsert { get; set; }

    public bool CancelOnUpsert { get; set; }

    public int StoreCalls { get; private set; }

    public void EnsureCreated() => StoreCalls++;

    public void VerifyWritable() => StoreCalls++;

    public void Upsert(GithubCopilotConnection connection)
    {
        StoreCalls++;
        if (CancelOnUpsert)
        {
            throw new OperationCanceledException();
        }

        if (FailOnUpsert)
        {
            FailOnUpsert = false;
            throw new IOException("disk full");
        }

        _rows[connection.Prefix] = connection;
    }

    public GithubCopilotConnection? Get(string prefix)
    {
        StoreCalls++;
        return _rows.GetValueOrDefault(prefix);
    }

    public GithubCopilotConnection[] List()
    {
        StoreCalls++;
        return _rows.Values.OrderBy(static row => row.Prefix, StringComparer.Ordinal).ToArray();
    }

    public bool Exists(string prefix)
    {
        StoreCalls++;
        return _rows.ContainsKey(prefix);
    }

    public int Delete(string prefix)
    {
        StoreCalls++;
        return _rows.Remove(prefix) ? 1 : 0;
    }
}

internal sealed class FakeOAuthClient : IGithubOAuthClient
{
    public Queue<GithubTokenPollResult> PollResults { get; } = new();

    public int PollCalls { get; private set; }

    public int RefreshCalls { get; private set; }

    public GithubUser? User { get; set; } = new("octocat", 1);

    public GithubCopilotEntitlement? Entitlement { get; set; } =
        new(ChatEnabled: true, ApiOrigin: "https://api.githubcopilot.com");

    public GithubTokenPollResult RefreshResult { get; set; } = new(GithubTokenPollKind.Success, "access-2", "refresh-2");

    public DateTimeOffset ExpiresAt { get; set; } = new(2026, 10, 20, 12, 15, 0, TimeSpan.Zero);

    public int IntervalSeconds { get; set; } = 5;

    public bool RejectClient { get; set; }

    public Task<GithubDeviceCode> StartDeviceFlowAsync(string clientId, CancellationToken cancellationToken)
    {
        if (RejectClient)
        {
            throw new GithubCopilotClientRejectedException($"GitHub did not accept clientId '{clientId}' for device flow.");
        }

        return Task.FromResult(new GithubDeviceCode("device-secret", "ABCD-1234", "https://github.com/login/device", ExpiresAt, IntervalSeconds));
    }

    public Task<GithubTokenPollResult> PollTokenAsync(string clientId, string deviceCode, CancellationToken cancellationToken)
    {
        PollCalls++;
        return Task.FromResult(PollResults.Count > 0 ? PollResults.Dequeue() : new GithubTokenPollResult(GithubTokenPollKind.Pending));
    }

    public Task<GithubTokenPollResult> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken)
    {
        RefreshCalls++;
        return Task.FromResult(RefreshResult);
    }

    public Task<GithubUser?> GetUserAsync(string accessToken, CancellationToken cancellationToken) => Task.FromResult(User);

    public Task<GithubCopilotEntitlement?> GetCopilotEntitlementAsync(string accessToken, CancellationToken cancellationToken) =>
        Task.FromResult(Entitlement);
}

internal sealed class FakeModelsClient : IGithubCopilotModelsClient
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
