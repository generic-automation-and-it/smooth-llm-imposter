using System.Net;
using System.Net.Http.Json;
using SmoothLlmImposter.Application.Common.Clients.GithubCopilot;
using SmoothLlmImposter.Application.Features.GithubCopilot;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// L2 — the real Host with the Copilot feature enabled, a real temporary SQLite database/key ring, and only the
/// GitHub clients and upstream transport substituted. Covers the ordered workflow, publication readiness,
/// matched forwarding for both dialects, D1 ownership, revision-scoped 401 invalidation and restart recovery.
/// </summary>
public class GithubCopilotIntegrationTests
{
    [Fact]
    public async Task Flag_off_exposes_no_copilot_endpoints_and_creates_no_database()
    {
        using var fixture = new GithubCopilotAppFixture(enabled: false);
        using HttpClient client = fixture.CreateAdminClient();

        (await client.GetAsync("/admin/github-copilot/connections", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync(
                "/admin/github-copilot/logins",
                new { prefix = "company1", clientId = "client" },
                TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        File.Exists(fixture.DatabasePath).ShouldBeFalse();
        Directory.Exists(Path.Combine(fixture.DataDirectory, "keys")).ShouldBeFalse();
    }

    [Fact]
    public async Task Complete_before_the_polling_interval_returns_202_with_retry_after_and_a_next_step_message()
    {
        using var fixture = new GithubCopilotAppFixture();
        using HttpClient client = fixture.CreateAdminClient();
        GithubCopilotLoginStartResponse start = await GithubCopilotAppFixture.StartLoginAsync(client, "company1", "client");

        start.Message.ShouldContain(start.VerificationUri);
        start.Message.ShouldContain(start.UserCode);
        start.ProviderKeys.ShouldBe(["company1-github-anthropic", "company1-github-openai"]);

        using HttpResponseMessage response = await client.PostAsync(
            $"/admin/github-copilot/logins/{start.LoginId}/complete",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.RetryAfter!.Delta!.Value.ShouldBe(TimeSpan.FromSeconds(5));
        GithubCopilotLoginCompleteResponse? body = await response.Content.ReadFromJsonAsync<GithubCopilotLoginCompleteResponse>(TestContext.Current.CancellationToken);
        body!.Outcome.ShouldBe("pending");
        body.Message.ShouldContain("Authorization still pending");
        body.Message.ShouldNotContain("device-secret");
        fixture.OAuth.PollCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_publication_failure_returns_503_with_the_saved_revision_and_a_restart_hint()
    {
        using var fixture = new GithubCopilotAppFixture(collideReservedName: true);
        using HttpClient client = fixture.CreateAdminClient();
        GithubCopilotLoginStartResponse start = await GithubCopilotAppFixture.StartLoginAsync(client, "company1", "client");
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        fixture.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.Success, "access-token", "refresh-token"));

        using HttpResponseMessage response = await client.PostAsync(
            $"/admin/github-copilot/logins/{start.LoginId}/complete",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        GithubCopilotLoginCompleteResponse? body = await response.Content.ReadFromJsonAsync<GithubCopilotLoginCompleteResponse>(TestContext.Current.CancellationToken);
        body!.Outcome.ShouldBe("saved_not_published");
        body.SavedRevision.ShouldBe(1);
        body.PublishedRevision.ShouldBeNull();
        body.PublicationReady.ShouldBeFalse();
        body.Message.ShouldContain("restart re-materializes");
        body.Message.ShouldNotContain("access-token");
    }

    [Fact]
    public async Task An_unknown_login_id_returns_404_with_a_start_new_login_message()
    {
        using var fixture = new GithubCopilotAppFixture();
        using HttpClient client = fixture.CreateAdminClient();

        using HttpResponseMessage response = await client.PostAsync(
            "/admin/github-copilot/logins/does-not-exist/complete",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        GithubCopilotLoginCompleteResponse? body = await response.Content.ReadFromJsonAsync<GithubCopilotLoginCompleteResponse>(TestContext.Current.CancellationToken);
        body!.ErrorCode.ShouldBe("login_not_found");
        body.Message.ShouldContain("Start a new login");
    }

    [Fact]
    public async Task A_denied_grant_returns_400_with_a_terminal_message()
    {
        using var fixture = new GithubCopilotAppFixture();
        using HttpClient client = fixture.CreateAdminClient();
        GithubCopilotLoginStartResponse start = await GithubCopilotAppFixture.StartLoginAsync(client, "company1", "client");
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        fixture.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.Denied));

        using HttpResponseMessage response = await client.PostAsync(
            $"/admin/github-copilot/logins/{start.LoginId}/complete",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        GithubCopilotLoginCompleteResponse? body = await response.Content.ReadFromJsonAsync<GithubCopilotLoginCompleteResponse>(TestContext.Current.CancellationToken);
        body!.Outcome.ShouldBe("failed");
        body.ErrorCode.ShouldBe("access_denied");
        body.Message.ShouldContain("denied the authorization request");
    }

    [Fact]
    public async Task Login_publishes_the_pair_and_a_mapped_route_forwards_both_dialects_with_the_managed_token()
    {
        using var fixture = new GithubCopilotAppFixture();
        using HttpClient client = fixture.CreateAdminClient();

        string connectionId = await LoginAsync(fixture, client);
        await ConfigureMappingAsync(client, "company1-github-anthropic", "anthropic", "claude-*", "claude-haiku-4.5");
        await ConfigureMappingAsync(client, "company1-github-openai", "openai", "gpt-*", "gpt-5.3-codex");

        // Anthropic: /v1/messages stays /v1/messages at the account-discovered origin.
        int before = fixture.Upstream.RequestCount;
        using HttpResponseMessage anthropic = await client.PostAsJsonAsync(
            "/anthropic/v1/messages",
            new { model = "claude-sonnet-4.6", max_tokens = 16, messages = new[] { new { role = "user", content = "hi" } } },
            TestContext.Current.CancellationToken);
        anthropic.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.RequestCount.ShouldBe(before + 1);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://api.githubcopilot.com/v1/messages");
        fixture.Upstream.LastAuthorization.ShouldBe("Bearer access-token");
        fixture.Upstream.LastHeaders.ContainsKey("X-GitHub-Api-Version").ShouldBeTrue();
        fixture.Upstream.LastHeaders["X-GitHub-Api-Version"].ShouldBe("2025-04-01");

        // OpenAI Responses: inbound /v1/responses becomes /responses.
        using HttpResponseMessage openai = await client.PostAsJsonAsync(
            "/openai/v1/responses",
            new { model = "gpt-5.5", input = "hi" },
            TestContext.Current.CancellationToken);
        openai.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://api.githubcopilot.com/responses");
        fixture.Upstream.LastAuthorization.ShouldBe("Bearer access-token");
        fixture.Upstream.LastHeaders.ContainsKey("Openai-Intent").ShouldBeTrue();

        // Status step 05 reports publication and routing readiness, and never leaks the token.
        GithubCopilotConnectionStatus? status = await GetConnectionAsync(client, connectionId);
        status!.PublicationReady.ShouldBeTrue();
        status.Providers.ShouldAllBe(provider => provider.RoutingReady);
        status.Message.ShouldContain("routing-ready");
    }

    [Fact]
    public async Task A_copilot_chat_path_is_rejected_before_forwarding()
    {
        using var fixture = new GithubCopilotAppFixture();
        using HttpClient client = fixture.CreateAdminClient();
        await LoginAsync(fixture, client);
        await ConfigureMappingAsync(client, "company1-github-openai", "openai", "gpt-*", "gpt-5.3-codex");
        int before = fixture.Upstream.RequestCount;
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/openai/v1/chat/completions",
            new { model = "gpt-5.5", messages = new[] { new { role = "user", content = "hi" } } },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        fixture.Upstream.RequestCount.ShouldBe(before);

    }

    [Fact]
    public async Task Generic_crud_cannot_create_or_delete_a_connected_reserved_provider()
    {
        using var fixture = new GithubCopilotAppFixture();
        using HttpClient client = fixture.CreateAdminClient();
        await LoginAsync(fixture, client);

        // A prefix with no login is not materialized, so a generic PUT may not conjure it.
        using HttpResponseMessage create = await client.PutAsJsonAsync(
            "/admin/providers/company2-github-openai",
            Body("openai", "gpt-*", "gpt-5.3-codex"),
            TestContext.Current.CancellationToken);
        create.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using HttpResponseMessage delete = await client.DeleteAsync(
            "/admin/providers/company1-github-anthropic",
            TestContext.Current.CancellationToken);
        delete.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_upstream_401_invalidates_only_this_revision_and_routing_then_fails_closed()
    {
        using var fixture = new GithubCopilotAppFixture();
        using HttpClient client = fixture.CreateAdminClient();
        string connectionId = await LoginAsync(fixture, client);
        await ConfigureMappingAsync(client, "company1-github-anthropic", "anthropic", "claude-*", "claude-haiku-4.5");

        fixture.Upstream.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        using HttpResponseMessage rejected = await client.PostAsJsonAsync(
            "/anthropic/v1/messages",
            new { model = "claude-sonnet-4.6", max_tokens = 16, messages = new[] { new { role = "user", content = "hi" } } },
            TestContext.Current.CancellationToken);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        GithubCopilotConnectionStatus? status = await GetConnectionAsync(client, connectionId);
        status!.ReauthenticationRequired.ShouldBeTrue();
        status.Message.ShouldContain("requires reauthorization");

        int before = fixture.Upstream.RequestCount;
        using HttpResponseMessage next = await client.PostAsJsonAsync(
            "/anthropic/v1/messages",
            new { model = "claude-sonnet-4.6", max_tokens = 16, messages = new[] { new { role = "user", content = "hi" } } },
            TestContext.Current.CancellationToken);
        next.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        fixture.Upstream.RequestCount.ShouldBe(before);
    }

    [Fact]
    public async Task Model_discovery_filters_chat_only_models_and_points_at_the_mapping_step()
    {
        using var fixture = new GithubCopilotAppFixture();
        using HttpClient client = fixture.CreateAdminClient();
        string connectionId = await LoginAsync(fixture, client);

        GithubCopilotModelsDiscoveryResponse? response = await client.GetFromJsonAsync<GithubCopilotModelsDiscoveryResponse>(
            $"/admin/github-copilot/connections/{connectionId}/models",
            TestContext.Current.CancellationToken);

        response.ShouldNotBeNull();
        response.Models.Select(static model => model.Id).ShouldBe(["claude-haiku-4.5", "gpt-5.3-codex"]);
        response.Message.ShouldContain("PUT /admin/providers/");
        response.Message.ShouldContain("Discovery does not create mappings");
    }

    [Fact]
    public async Task Restart_recovers_the_connection_and_routes_without_another_login()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"copilot-restart-{Guid.NewGuid():N}", "copilot.db");
        string connectionId;

        using (var first = new GithubCopilotAppFixture(databasePath: databasePath, deleteDataOnDispose: false))
        {
            using HttpClient client = first.CreateAdminClient();
            connectionId = await LoginAsync(first, client);
        }

        using var restarted = new GithubCopilotAppFixture(
            databasePath: databasePath,
            seedReservedProviders: true,
            seedReservedMappings: true);
        using HttpClient admin = restarted.CreateAdminClient();

        GithubCopilotConnectionListResponse? list = await admin.GetFromJsonAsync<GithubCopilotConnectionListResponse>(
            "/admin/github-copilot/connections",
            TestContext.Current.CancellationToken);
        list!.Connections.Single().ConnectionId.ShouldBe(connectionId);
        list.Connections.Single().PublicationReady.ShouldBeTrue();

        using HttpResponseMessage forwarded = await admin.PostAsJsonAsync(
            "/anthropic/v1/messages",
            new { model = "claude-sonnet-4.6", max_tokens = 16, messages = new[] { new { role = "user", content = "hi" } } },
            TestContext.Current.CancellationToken);

        forwarded.StatusCode.ShouldBe(HttpStatusCode.OK);
        restarted.Upstream.LastAuthorization.ShouldBe("Bearer access-token");
        restarted.OAuth.PollCalls.ShouldBe(0);
    }

    private static async Task<string> LoginAsync(GithubCopilotAppFixture fixture, HttpClient client)
    {
        GithubCopilotLoginStartResponse start = await GithubCopilotAppFixture.StartLoginAsync(client, "company1", "client");
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        fixture.OAuth.PollResults.Enqueue(new GithubTokenPollResult(GithubTokenPollKind.Success, "access-token", "refresh-token"));

        using HttpResponseMessage response = await client.PostAsync(
            $"/admin/github-copilot/logins/{start.LoginId}/complete",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        GithubCopilotLoginCompleteResponse? body = await response.Content.ReadFromJsonAsync<GithubCopilotLoginCompleteResponse>(TestContext.Current.CancellationToken);
        body!.PublicationReady.ShouldBeTrue();
        body.ConnectionId.ShouldNotBeNullOrWhiteSpace();
        return body.ConnectionId!;
    }

    private static async Task ConfigureMappingAsync(HttpClient client, string key, string dialect, string from, string to)
    {
        using HttpResponseMessage response = await client.PutAsJsonAsync(
            $"/admin/providers/{key}",
            Body(dialect, from, to),
            TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static object Body(string dialect, string from, string to) => new
    {
        name = (string?)null,
        dialect,
        baseUrl = "https://api.githubcopilot.com",
        authScheme = "Bearer",
        authHeader = (string?)null,
        isDefault = false,
        enabled = true,
        anthropicVersion = (string?)null,
        openAiUpstreamApi = "responses",
        requestNormalization = "none",
        sessionForwarding = (string?)null,
        stripEncryptedContent = (bool?)null,
        timeoutSeconds = (int?)null,
        models = new[] { new { from, to, caching = false } }
    };

    private static async Task<GithubCopilotConnectionStatus?> GetConnectionAsync(HttpClient client, string connectionId) =>
        await client.GetFromJsonAsync<GithubCopilotConnectionStatus>(
            $"/admin/github-copilot/connections/{connectionId}",
            TestContext.Current.CancellationToken);
}
