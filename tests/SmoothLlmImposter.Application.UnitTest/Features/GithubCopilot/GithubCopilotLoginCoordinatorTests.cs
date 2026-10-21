using FluentValidation;
using SmoothLlmImposter.Application.Common.Clients.GithubCopilot;
using SmoothLlmImposter.Application.Features.GithubCopilot;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Features.GithubCopilot;

/// <summary>
/// L0 — the device-login state machine and client-ID resolution. Every case uses a manual clock and a
/// controllable OAuth client: no sleeps, no network, no real SQLite.
/// </summary>
public class GithubCopilotLoginCoordinatorTests
{
    private static async Task<GithubCopilotLoginStartResponse> StartAsync(GithubCopilotHarness harness, string? prefix = "company1", string? clientId = null) =>
        await harness.Coordinator.StartAsync(prefix, clientId, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Start_requires_a_client_id_when_no_input_or_fallback_resolves()
    {
        var harness = new GithubCopilotHarness(clientIdFallback: null);

        ValidationException ex = await Should.ThrowAsync<ValidationException>(
            () => StartAsync(harness, clientId: null));

        ex.Errors.ShouldContain(failure => failure.PropertyName == "clientId");
    }

    [Fact]
    public async Task Start_rejects_an_explicitly_blank_client_id_instead_of_falling_back()
    {
        var harness = new GithubCopilotHarness();

        ValidationException ex = await Should.ThrowAsync<ValidationException>(
            () => StartAsync(harness, clientId: "   "));

        ex.Errors.ShouldContain(failure => failure.PropertyName == "clientId");
    }

    [Fact]
    public async Task Start_returns_a_field_level_error_when_github_rejects_the_client_id()
    {
        var harness = new GithubCopilotHarness();
        harness.OAuth.RejectClient = true;

        ValidationException ex = await Should.ThrowAsync<ValidationException>(
            () => StartAsync(harness, clientId: "not-a-real-client"));

        ex.Errors.ShouldContain(failure => failure.PropertyName == "clientId");
        harness.Store.Exists("company1").ShouldBeFalse();
    }

    [Fact]
    public async Task Start_uses_the_explicit_client_id_over_the_fallback()
    {
        var harness = new GithubCopilotHarness();
        harness.Store.Upsert(SavedConnection("company1", clientId: "saved-client"));

        GithubCopilotLoginStartResponse response = await StartAsync(harness, clientId: "explicit-client");

        response.ClientId.ShouldBe("explicit-client");
        response.Prefix.ShouldBe("company1");
        response.ProviderKeys.ShouldBe(["company1-github-anthropic", "company1-github-openai"]);
        response.VerificationUri.ShouldBe("https://github.com/login/device");
        response.UserCode.ShouldBe("ABCD-1234");
        response.IntervalSeconds.ShouldBe(5);
        response.CompleteUrl.ShouldBe($"/admin/github-copilot/logins/{response.LoginId}/complete");
        response.Message.ShouldContain("https://github.com/login/device");
        response.Message.ShouldContain("ABCD-1234");
    }

    [Fact]
    public async Task Start_prefers_the_saved_prefix_client_id_over_the_fallback()
    {
        var harness = new GithubCopilotHarness();
        harness.Store.Upsert(SavedConnection("company1", clientId: "saved-client"));

        GithubCopilotLoginStartResponse response = await StartAsync(harness, clientId: null);

        response.ClientId.ShouldBe("saved-client");
    }

    [Fact]
    public async Task Start_rejects_a_second_pending_login_for_the_same_prefix()
    {
        var harness = new GithubCopilotHarness();
        await StartAsync(harness);

        await Should.ThrowAsync<GithubCopilotProviderConflictException>(() => StartAsync(harness));
    }

    [Fact]
    public async Task Complete_waits_out_the_polling_interval_without_calling_github()
    {
        var harness = new GithubCopilotHarness();
        GithubCopilotLoginStartResponse start = await StartAsync(harness);

        GithubCopilotLoginCompleteResponse response = await harness.Coordinator.CompleteAsync(start.LoginId, TestContext.Current.CancellationToken);

        response.Status.ShouldBe(GithubCopilotCompleteOutcome.Pending);
        response.RetryAfterSeconds.ShouldBe(5);
        response.Message.ShouldContain("Authorization still pending");
        harness.OAuth.PollCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Complete_reports_pending_after_a_due_poll()
    {
        var harness = new GithubCopilotHarness();
        GithubCopilotLoginStartResponse start = await StartAsync(harness);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        harness.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.Pending));

        GithubCopilotLoginCompleteResponse response = await harness.Coordinator.CompleteAsync(start.LoginId, TestContext.Current.CancellationToken);

        response.Status.ShouldBe(GithubCopilotCompleteOutcome.Pending);
        response.RetryAfterSeconds.ShouldBe(5);
        harness.OAuth.PollCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Complete_honours_slow_down_by_raising_the_server_side_interval()
    {
        var harness = new GithubCopilotHarness();
        GithubCopilotLoginStartResponse start = await StartAsync(harness);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        harness.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.SlowDown, IntervalSeconds: 10));

        GithubCopilotLoginCompleteResponse response = await harness.Coordinator.CompleteAsync(start.LoginId, TestContext.Current.CancellationToken);

        response.Status.ShouldBe(GithubCopilotCompleteOutcome.Pending);
        response.RetryAfterSeconds.ShouldBe(15); // 10 from GitHub + the 5 s slow-down penalty
    }

    [Fact]
    public async Task Complete_fails_terminally_when_the_user_denies()
    {
        var harness = new GithubCopilotHarness();
        GithubCopilotLoginStartResponse start = await StartAsync(harness);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        harness.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.Denied));

        GithubCopilotLoginCompleteResponse response = await harness.Coordinator.CompleteAsync(start.LoginId, TestContext.Current.CancellationToken);

        response.Status.ShouldBe(GithubCopilotCompleteOutcome.Failed);
        response.ErrorCode.ShouldBe("access_denied");
        response.Message.ShouldStartWith("The GitHub user denied");
        harness.Store.Exists("company1").ShouldBeFalse();
    }

    [Fact]
    public async Task Complete_fails_when_the_device_code_expires_before_approval()
    {
        var harness = new GithubCopilotHarness();
        GithubCopilotLoginStartResponse start = await StartAsync(harness);
        harness.Time.Advance(TimeSpan.FromMinutes(20));

        GithubCopilotLoginCompleteResponse response = await harness.Coordinator.CompleteAsync(start.LoginId, TestContext.Current.CancellationToken);

        response.Status.ShouldBe(GithubCopilotCompleteOutcome.Failed);
        response.ErrorCode.ShouldBe("expired_token");
        harness.OAuth.PollCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Complete_refuses_a_login_the_account_is_not_entitled_to()
    {
        var harness = new GithubCopilotHarness();
        GithubCopilotLoginStartResponse start = await StartAsync(harness);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        harness.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.Success, "access-1", "refresh-1"));
        harness.OAuth.Entitlement = new GithubCopilotEntitlement(ChatEnabled: false, ApiOrigin: "https://api.githubcopilot.com");

        GithubCopilotLoginCompleteResponse response = await harness.Coordinator.CompleteAsync(start.LoginId, TestContext.Current.CancellationToken);

        response.Status.ShouldBe(GithubCopilotCompleteOutcome.Failed);
        response.ErrorCode.ShouldBe("copilot_not_enabled");
        harness.Store.Exists("company1").ShouldBeFalse();
    }

    [Fact]
    public async Task Complete_refuses_an_api_origin_outside_the_github_allowlist()
    {
        var harness = new GithubCopilotHarness();
        GithubCopilotLoginStartResponse start = await StartAsync(harness);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        harness.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.Success, "access-1", "refresh-1"));
        harness.OAuth.Entitlement = new GithubCopilotEntitlement(ChatEnabled: true, ApiOrigin: "https://evil.example");

        GithubCopilotLoginCompleteResponse response = await harness.Coordinator.CompleteAsync(start.LoginId, TestContext.Current.CancellationToken);

        response.Status.ShouldBe(GithubCopilotCompleteOutcome.Failed);
        response.ErrorCode.ShouldBe("invalid_api_origin");
        harness.Store.Exists("company1").ShouldBeFalse();
    }

    [Fact]
    public async Task Complete_saves_and_publishes_both_provider_keys_on_success()
    {
        var harness = new GithubCopilotHarness();
        GithubCopilotLoginStartResponse start = await StartAsync(harness);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        harness.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.Success, "access-1", "refresh-1", harness.Time.ExpiresAtUtc()));

        GithubCopilotLoginCompleteResponse response = await harness.Coordinator.CompleteAsync(start.LoginId, TestContext.Current.CancellationToken);

        response.Status.ShouldBe(GithubCopilotCompleteOutcome.Succeeded);
        response.PublicationReady.ShouldBeTrue();
        response.SavedRevision.ShouldBe(1);
        response.PublishedRevision.ShouldBe(1);
        response.ConnectionId.ShouldNotBeNullOrWhiteSpace();
        response.Message.ShouldContain("saved and published");
        response.Message.ShouldContain("PUT /admin/providers/company1-github-anthropic");
        response.Message.ShouldNotContain("access-1");

        harness.Store.Get("company1").ShouldNotBeNull();
        harness.Store.Get("company1")!.AccessTokenCiphertext.ShouldBe("enc:access-1");
        harness.Store.Get("company1")!.RefreshTokenCiphertext.ShouldBe("enc:refresh-1");

        GithubCopilotPublishedConnection published = harness.Connections.GetByPrefix("company1")!;
        published.AccessToken.ShouldBe("access-1");
        published.ApiOrigin.ShouldBe("https://api.githubcopilot.com");
        published.Revision.ShouldBe(1);

        harness.Registry.TryGet("company1-github-anthropic", out ProviderOptions? anthropic).ShouldBeTrue();
        anthropic!.Dialect.ShouldBe("anthropic");
        anthropic.AuthScheme.ShouldBe("Bearer");
        anthropic.AuthHeader.ShouldBeNull();
        anthropic.Secret.ShouldBeNull();
        anthropic.IsDefault.ShouldBeFalse();

        harness.Registry.TryGet("company1-github-openai", out ProviderOptions? openai).ShouldBeTrue();
        openai!.Dialect.ShouldBe("openai");
        openai.OpenAiUpstreamApi.ShouldBe("responses");
        openai.RequestNormalization.ShouldBe("none");
    }

    [Fact]
    public async Task A_retried_complete_after_success_returns_the_same_identity_without_contacting_github()
    {
        var harness = new GithubCopilotHarness();
        GithubCopilotLoginStartResponse start = await StartAsync(harness);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        harness.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.Success, "access-1", "refresh-1"));

        GithubCopilotLoginCompleteResponse first = await harness.Coordinator.CompleteAsync(start.LoginId, TestContext.Current.CancellationToken);
        int pollCalls = harness.OAuth.PollCalls;
        GithubCopilotLoginCompleteResponse second = await harness.Coordinator.CompleteAsync(start.LoginId, TestContext.Current.CancellationToken);

        second.Status.ShouldBe(GithubCopilotCompleteOutcome.Succeeded);
        second.ConnectionId.ShouldBe(first.ConnectionId);
        second.SavedRevision.ShouldBe(first.SavedRevision);
        harness.OAuth.PollCalls.ShouldBe(pollCalls);
        harness.Store.List().Length.ShouldBe(1);
    }

    [Fact]
    public async Task A_save_failure_is_retryable_and_the_retry_never_re_exchanges_the_device_grant()
    {
        var harness = new GithubCopilotHarness();
        GithubCopilotLoginStartResponse start = await StartAsync(harness);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        harness.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.Success, "access-1", "refresh-1"));
        harness.Store.FailOnUpsert = true;

        GithubCopilotLoginCompleteResponse first = await harness.Coordinator.CompleteAsync(start.LoginId, TestContext.Current.CancellationToken);

        first.Status.ShouldBe(GithubCopilotCompleteOutcome.Retryable);
        first.ErrorCode.ShouldBe("save_failed");

        GithubCopilotLoginCompleteResponse second = await harness.Coordinator.CompleteAsync(start.LoginId, TestContext.Current.CancellationToken);

        second.Status.ShouldBe(GithubCopilotCompleteOutcome.Succeeded);
        harness.OAuth.PollCalls.ShouldBe(1);
    }

    [Fact]
    public async Task A_cancelled_save_propagates_cancellation_instead_of_reporting_a_retryable_failure()
    {
        var harness = new GithubCopilotHarness();
        GithubCopilotLoginStartResponse start = await StartAsync(harness);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        harness.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.Success, "access-1", "refresh-1"));
        harness.Store.CancelOnUpsert = true;

        await Should.ThrowAsync<OperationCanceledException>(
            () => harness.Coordinator.CompleteAsync(start.LoginId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Publication_failure_returns_503_with_the_saved_revision_and_a_retry_republishes_only()
    {
        var harness = new GithubCopilotHarness();
        // A name collision makes the whole registry invalid, so publication is refused after the row is saved.
        harness.Registry.Seed(new Dictionary<string, ProviderOptions>(StringComparer.Ordinal)
        {
            ["ordinary"] = new() { Name = "company1-github-anthropic", Dialect = "openai", BaseUrl = "https://ordinary.example" }
        });

        GithubCopilotLoginStartResponse start = await StartAsync(harness);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        harness.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.Success, "access-1", "refresh-1"));

        GithubCopilotLoginCompleteResponse first = await harness.Coordinator.CompleteAsync(start.LoginId, TestContext.Current.CancellationToken);

        first.Status.ShouldBe(GithubCopilotCompleteOutcome.SavedNotPublished);
        first.SavedRevision.ShouldBe(1);
        first.PublishedRevision.ShouldBeNull();
        first.PublicationReady.ShouldBeFalse();
        first.Message.ShouldContain("restart re-materializes");
        harness.Store.Exists("company1").ShouldBeTrue();

        harness.Registry.Delete("ordinary");
        int pollCalls = harness.OAuth.PollCalls;
        GithubCopilotLoginCompleteResponse second = await harness.Coordinator.CompleteAsync(start.LoginId, TestContext.Current.CancellationToken);

        second.Status.ShouldBe(GithubCopilotCompleteOutcome.Succeeded);
        second.PublishedRevision.ShouldBe(1);
        harness.OAuth.PollCalls.ShouldBe(pollCalls);
    }

    [Fact]
    public async Task A_relogin_keeps_operator_mappings_and_a_disabled_provider_while_incrementing_the_revision()
    {
        var harness = new GithubCopilotHarness();
        GithubCopilotLoginStartResponse start = await StartAsync(harness);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        harness.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.Success, "access-1", "refresh-1"));
        await harness.Coordinator.CompleteAsync(start.LoginId, TestContext.Current.CancellationToken);

        harness.Registry.TryGet("company1-github-anthropic", out ProviderOptions? existing);
        ProviderOptions edited = ProviderOptionsCloner.Clone(existing!);
        edited.Enabled = false;
        edited.Models.Clear();
        edited.Models.Add(new ModelMappingOptions { From = "claude-*", To = "claude-haiku-4.5" });
        harness.Registry.Upsert("company1-github-anthropic", edited);

        harness.Time.Advance(TimeSpan.FromMinutes(1));
        GithubCopilotLoginStartResponse second = await StartAsync(harness, clientId: "explicit-client");
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        harness.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.Success, "access-2", "refresh-2"));
        GithubCopilotLoginCompleteResponse completed = await harness.Coordinator.CompleteAsync(second.LoginId, TestContext.Current.CancellationToken);

        completed.Status.ShouldBe(GithubCopilotCompleteOutcome.Succeeded);
        completed.SavedRevision.ShouldBe(2);
        harness.Registry.TryGet("company1-github-anthropic", out ProviderOptions? reloaded).ShouldBeTrue();
        reloaded!.Enabled.ShouldBeFalse();
        reloaded.Models.Single().To.ShouldBe("claude-haiku-4.5");
        reloaded.Secret.ShouldBeNull();
        harness.Connections.GetByPrefix("company1")!.AccessToken.ShouldBe("access-2");
    }

    [Fact]
    public async Task Separate_prefixes_do_not_share_credentials_or_provider_keys()
    {
        var harness = new GithubCopilotHarness();

        GithubCopilotLoginStartResponse company = await StartAsync(harness, "company1");
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        harness.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.Success, "access-company", "refresh-company"));
        await harness.Coordinator.CompleteAsync(company.LoginId, TestContext.Current.CancellationToken);

        GithubCopilotLoginStartResponse other = await StartAsync(harness, "company2");
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        harness.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.Success, "access-other", "refresh-other"));
        await harness.Coordinator.CompleteAsync(other.LoginId, TestContext.Current.CancellationToken);

        harness.Connections.GetByPrefix("company1")!.AccessToken.ShouldBe("access-company");
        harness.Connections.GetByPrefix("company2")!.AccessToken.ShouldBe("access-other");
        harness.Registry.TryGet("company2-github-anthropic", out _).ShouldBeTrue();
        harness.Registry.TryGet("company1-github-anthropic", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Status_reports_publication_ready_but_not_routing_ready_until_a_mapping_exists()
    {
        var harness = new GithubCopilotHarness();
        GithubCopilotLoginStartResponse start = await StartAsync(harness);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        harness.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.Success, "access-1", "refresh-1"));
        await harness.Coordinator.CompleteAsync(start.LoginId, TestContext.Current.CancellationToken);

        GithubCopilotConnectionStatus status = harness.StatusReader().List().Single();

        status.PublicationReady.ShouldBeTrue();
        status.Providers.ShouldAllBe(provider => provider.Enabled && !provider.RoutingReady);
        status.Message.ShouldContain("add a From/To model mapping");
        status.Message.ShouldContain("A restart is not required");
    }

    private static GithubCopilotConnection SavedConnection(string prefix, string clientId) =>
        new(
            Prefix: prefix,
            ConnectionId: "saved-connection",
            GitHubUser: "octocat",
            ClientId: clientId,
            ApiOrigin: "https://api.githubcopilot.com",
            AccessTokenCiphertext: "enc:old-access",
            RefreshTokenCiphertext: "enc:old-refresh",
            ExpiresAtUtc: null,
            Revision: 3);
}

internal static class GithubCopilotTimeExtensions
{
    public static DateTimeOffset ExpiresAtUtc(this ManualTimeProvider time) => time.GetUtcNow() + TimeSpan.FromHours(8);
}
