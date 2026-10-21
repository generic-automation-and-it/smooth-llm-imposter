using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SmoothLlmImposter.Application.Common.Clients.GithubCopilot;
using SmoothLlmImposter.Application.Features.GithubCopilot;
using SmoothLlmImposter.Infrastructure.Clients.GithubCopilot;

namespace SmoothLlmImposter.Infrastructure.UnitTest.Clients;

/// <summary>
/// L0 — the token endpoint's classification contract. A server failure or rate limit must reach the login/refresh
/// state machines as retryable (an exception), never as a terminal grant verdict; a real error body is classified.
/// </summary>
public class GithubOAuthClientTests
{
    private static GithubOAuthClient Client(HttpStatusCode status, string body) =>
        new(
            new SingleClientFactory(new StubHandler(status, body)),
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<GithubOAuthClient>.Instance);

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task A_server_failure_on_poll_is_retryable_not_a_grant_verdict(HttpStatusCode status)
    {
        GithubOAuthClient client = Client(status, "upstream down");

        await Should.ThrowAsync<GithubCopilotUpstreamException>(
            () => client.PollTokenAsync("client", "device", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_server_failure_on_refresh_is_retryable_so_the_old_pair_is_not_discarded(HttpStatusCode status)
    {
        GithubOAuthClient client = Client(status, "upstream down");

        await Should.ThrowAsync<GithubCopilotUpstreamException>(
            () => client.RefreshAsync("client", "refresh", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task A_server_failure_with_a_parsable_body_is_still_retryable_not_a_grant_verdict(HttpStatusCode status)
    {
        // A parsable body would classify as Pending/Success if the status guard were missing, so this is the
        // case that proves the status arm itself rather than the null-body arm.
        GithubOAuthClient client = Client(status, """{"error":"authorization_pending"}""");

        await Should.ThrowAsync<GithubCopilotUpstreamException>(
            () => client.PollTokenAsync("client", "device", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<GithubCopilotUpstreamException>(
            () => client.RefreshAsync("client", "refresh", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_unreadable_success_body_is_retryable()
    {
        GithubOAuthClient client = Client(HttpStatusCode.OK, "<html>proxy</html>");

        await Should.ThrowAsync<GithubCopilotUpstreamException>(
            () => client.PollTokenAsync("client", "device", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_pending_grant_is_classified_pending()
    {
        GithubOAuthClient client = Client(HttpStatusCode.OK, """{"error":"authorization_pending"}""");

        GithubTokenPollResult result = await client.PollTokenAsync("client", "device", TestContext.Current.CancellationToken);

        result.Kind.ShouldBe(GithubTokenPollKind.Pending);
        result.AccessToken.ShouldBeNull();
    }

    [Fact]
    public async Task A_successful_poll_carries_the_pair_and_a_clock_based_expiry()
    {
        GithubOAuthClient client = Client(
            HttpStatusCode.OK,
            """{"access_token":"token-1","refresh_token":"refresh-1","expires_in":3600,"interval":7}""");

        GithubTokenPollResult result = await client.PollTokenAsync("client", "device", TestContext.Current.CancellationToken);

        result.Kind.ShouldBe(GithubTokenPollKind.Success);
        result.AccessToken.ShouldBe("token-1");
        result.RefreshToken.ShouldBe("refresh-1");
        result.IntervalSeconds.ShouldBe(7);
        result.AccessTokenExpiresAtUtc.ShouldBe(new DateTimeOffset(2026, 10, 20, 13, 0, 0, TimeSpan.Zero));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
