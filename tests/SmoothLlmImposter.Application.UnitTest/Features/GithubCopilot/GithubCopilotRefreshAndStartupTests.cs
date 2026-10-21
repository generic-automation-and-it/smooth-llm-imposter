using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.GithubCopilot;
using SmoothLlmImposter.Application.Features.GithubCopilot;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Features.GithubCopilot;

/// <summary>
/// L0 — refresh-ahead rotation and startup re-materialization (D2/R8). Both run off the inference path and are
/// driven by injected time and client substitutes, so no sleeps or network are involved.
/// </summary>
public class GithubCopilotRefreshAndStartupTests
{
    private static GithubCopilotRefreshCoordinator Refresher(GithubCopilotHarness harness) =>
        new(
            harness.OAuth,
            harness.Store,
            harness.Protector,
            harness.Publisher,
            harness.Connections,
            harness.Gate,
            Options.Create(harness.Options),
            harness.Time,
            NullLogger<GithubCopilotRefreshCoordinator>.Instance);

    private static GithubCopilotConnection SavedRow(DateTimeOffset expiresAt, long revision = 1, string? refreshToken = "enc:refresh-1") =>
        new(
            Prefix: "company1",
            ConnectionId: "conn",
            GitHubUser: "octocat",
            ClientId: "client",
            ApiOrigin: "https://api.githubcopilot.com",
            AccessTokenCiphertext: "enc:access-1",
            RefreshTokenCiphertext: refreshToken,
            ExpiresAtUtc: expiresAt.ToString("O"),
            Revision: revision);

    private static void SeedPublished(GithubCopilotHarness harness, GithubCopilotConnection row)
    {
        harness.Store.Upsert(row);
        harness.Publisher.Publish(row).Published.ShouldBeTrue();
    }

    [Fact]
    public async Task Refreshes_a_due_connection_and_publishes_the_next_revision()
    {
        var harness = new GithubCopilotHarness();
        SeedPublished(harness, SavedRow(harness.Time.GetUtcNow() + TimeSpan.FromMinutes(5)));
        harness.OAuth.RefreshResult = new GithubTokenPollResult(
            GithubTokenPollKind.Success,
            "access-2",
            "refresh-2",
            harness.Time.GetUtcNow() + TimeSpan.FromHours(8));

        int refreshed = await Refresher(harness).RefreshDueAsync(TestContext.Current.CancellationToken);

        refreshed.ShouldBe(1);
        harness.OAuth.RefreshCalls.ShouldBe(1);
        harness.Store.Get("company1")!.Revision.ShouldBe(2);
        harness.Store.Get("company1")!.AccessTokenCiphertext.ShouldBe("enc:access-2");
        harness.Connections.GetByPrefix("company1")!.AccessToken.ShouldBe("access-2");
        harness.Connections.GetByPrefix("company1")!.Revision.ShouldBe(2);
        harness.Connections.GetByPrefix("company1")!.State.ShouldBe(GithubCopilotConnectionState.Active);
    }

    [Fact]
    public async Task Does_not_refresh_a_connection_that_is_not_due()
    {
        var harness = new GithubCopilotHarness();
        SeedPublished(harness, SavedRow(harness.Time.GetUtcNow() + TimeSpan.FromHours(4)));

        int refreshed = await Refresher(harness).RefreshDueAsync(TestContext.Current.CancellationToken);

        refreshed.ShouldBe(0);
        harness.OAuth.RefreshCalls.ShouldBe(0);
    }

    [Fact]
    public async Task An_expiring_token_without_a_refresh_token_fails_closed_but_keeps_the_row()
    {
        var harness = new GithubCopilotHarness();
        SeedPublished(harness, SavedRow(harness.Time.GetUtcNow() + TimeSpan.FromMinutes(5), refreshToken: null));

        await Refresher(harness).RefreshDueAsync(TestContext.Current.CancellationToken);

        harness.Connections.GetByPrefix("company1")!.State.ShouldBe(GithubCopilotConnectionState.ReauthenticationRequired);
        harness.Store.Get("company1").ShouldNotBeNull();
        harness.OAuth.RefreshCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_terminal_refresh_failure_marks_reauthentication_required_without_deleting_the_row()
    {
        var harness = new GithubCopilotHarness();
        SeedPublished(harness, SavedRow(harness.Time.GetUtcNow() + TimeSpan.FromMinutes(5)));
        harness.OAuth.RefreshResult = new GithubTokenPollResult(GithubTokenPollKind.Rejected);

        await Refresher(harness).RefreshDueAsync(TestContext.Current.CancellationToken);

        harness.Connections.GetByPrefix("company1")!.State.ShouldBe(GithubCopilotConnectionState.ReauthenticationRequired);
        harness.Store.Get("company1")!.Revision.ShouldBe(1);
    }

    [Fact]
    public async Task A_rotated_pair_that_cannot_be_saved_fails_closed_instead_of_reusing_the_old_token()
    {
        var harness = new GithubCopilotHarness();
        SeedPublished(harness, SavedRow(harness.Time.GetUtcNow() + TimeSpan.FromMinutes(5)));
        harness.OAuth.RefreshResult = new GithubTokenPollResult(GithubTokenPollKind.Success, "access-2", "refresh-2");
        harness.Store.FailOnUpsert = true;

        await Refresher(harness).RefreshDueAsync(TestContext.Current.CancellationToken);

        harness.Connections.GetByPrefix("company1")!.State.ShouldBe(GithubCopilotConnectionState.ReauthenticationRequired);
    }

    [Fact]
    public void Startup_hydrates_a_saved_row_and_materializes_missing_pairs()
    {
        var harness = new GithubCopilotHarness();
        harness.Registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["ordinary"] = new() { Dialect = "openai", BaseUrl = "https://ordinary.example" }
        });
        harness.Store.Upsert(SavedRow(harness.Time.GetUtcNow() + TimeSpan.FromHours(1)));

        harness.Startup().Start();

        harness.Registry.TryGet("company1-github-anthropic", out _).ShouldBeTrue();
        harness.Registry.TryGet("company1-github-openai", out _).ShouldBeTrue();
        harness.Connections.GetByPrefix("company1")!.AccessToken.ShouldBe("access-1");
        harness.Registry.Snapshot().Keys.First().ShouldBe("ordinary"); // existing order preserved
    }

    [Fact]
    public void Startup_fails_clearly_when_a_saved_row_cannot_be_published()
    {
        var harness = new GithubCopilotHarness();
        harness.Registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["ordinary"] = new() { Name = "company1-github-anthropic", Dialect = "openai", BaseUrl = "https://ordinary.example" }
        });
        harness.Store.Upsert(SavedRow(harness.Time.GetUtcNow() + TimeSpan.FromHours(1)));

        Should.Throw<InvalidOperationException>(() => harness.Startup().Start());
    }

    [Fact]
    public void Startup_with_the_feature_disabled_opens_no_database()
    {
        var harness = new GithubCopilotHarness(enabled: false);

        harness.Startup().Start();

        harness.Store.StoreCalls.ShouldBe(0);
        harness.Connections.Snapshot().ShouldBeEmpty();
    }
}
