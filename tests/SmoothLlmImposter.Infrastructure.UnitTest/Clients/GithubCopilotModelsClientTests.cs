using System.Net;
using SmoothLlmImposter.Application.Common.Clients.GithubCopilot;
using SmoothLlmImposter.Infrastructure.Clients.GithubCopilot;

namespace SmoothLlmImposter.Infrastructure.UnitTest.Clients;

/// <summary>
/// L0 — the Copilot model catalog is reduced to the surfaces this proxy actually serves. A model that advertises
/// only chat endpoints must not be offered for Messages/Responses; an absent advertisement falls back to both.
/// </summary>
public class GithubCopilotModelsClientTests
{
    private const string Data = "https://api.githubcopilot.com";

    private static GithubCopilotModelsClient Client(string json) =>
        new(new SingleClientFactory(new StubHandler(json)));

    [Fact]
    public async Task A_chat_only_model_advertises_no_supported_surface()
    {
        GithubCopilotModel[] models = await Client(
            """{"data":[{"id":"gpt-chat","supported_endpoints":["/chat/completions"]}]}""")
            .ListModelsAsync("token", Data, TestContext.Current.CancellationToken);

        models.Single().Endpoints.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_absent_endpoint_advertisement_falls_back_to_both_surfaces()
    {
        GithubCopilotModel[] models = await Client("""{"data":[{"id":"mystery-model"}]}""")
            .ListModelsAsync("token", Data, TestContext.Current.CancellationToken);

        models.Single().Endpoints.ShouldBe(["messages", "responses"]);
    }

    [Fact]
    public async Task Advertised_endpoints_are_normalized_and_deduplicated()
    {
        GithubCopilotModel[] models = await Client(
            """{"data":[{"id":"claude-haiku-4.5","supported_endpoints":["/v1/messages","/messages","/chat/completions"]}]}""")
            .ListModelsAsync("token", Data, TestContext.Current.CancellationToken);

        models.Single().Endpoints.ShouldBe(["messages"]);
    }

    [Fact]
    public async Task The_catalog_may_be_a_bare_array_and_reads_model_policy()
    {
        GithubCopilotModel[] models = await Client(
            """[{"id":"gpt-5.3-codex","supported_endpoints":["/responses"],"policy":{"state":"enabled"}}]""")
            .ListModelsAsync("token", Data, TestContext.Current.CancellationToken);

        models.Single().Policy.ShouldBe("enabled");
        models.Single().Endpoints.ShouldBe(["responses"]);
    }

    [Fact]
    public async Task A_non_allowlisted_origin_is_refused_before_any_request()
    {
        var handler = new StubHandler("""{"data":[]}""");
        var client = new GithubCopilotModelsClient(new SingleClientFactory(handler));

        await Should.ThrowAsync<Application.Features.GithubCopilot.GithubCopilotUpstreamException>(
            () => client.ListModelsAsync("token", "https://evil.example", TestContext.Current.CancellationToken));

        // The allowlist exists so a managed token never leaves the process for a non-GitHub origin: assert no
        // request was attempted at all, not merely that the call threw.
        handler.Calls.ShouldBe(0);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
