using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// L2 coverage for HLD 005: <c>GET /openai/v1/models</c> is answered locally from the route catalogue
/// (distinct union of OpenAI <c>to</c> targets), makes no upstream call (NFR-03), and is byte-stable
/// (NFR-01). Anthropic discovery and non-GET methods on the OpenAI path stay transparent passthrough (LADR-03).
/// </summary>
public sealed class OpenAiModelsAggregationTests(ModelsCatalogAppFixture fixture) : IClassFixture<ModelsCatalogAppFixture>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Get_openai_models_returns_distinct_union_synthesized_without_upstream_call()
    {
        HttpClient client = fixture.CreateClient();
        int upstreamBefore = fixture.Upstream.RequestCount;

        using HttpResponseMessage response = await client.GetAsync("/openai/v1/models", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

        string body = await response.Content.ReadAsStringAsync(Ct);
        JsonObject root = JsonNode.Parse(body)!.AsObject();

        root["object"]!.GetValue<string>().ShouldBe("list");

        JsonArray data = root["data"]!.AsArray();
        // Distinct OpenAI `to` set in catalogue order; the duplicate "shared-model" collapses to one entry.
        data.Select(m => m!["id"]!.GetValue<string>())
            .ShouldBe(ModelsCatalogAppFixture.ExpectedOpenAiModelIds);

        foreach (JsonNode? entry in data)
        {
            entry!["object"]!.GetValue<string>().ShouldBe("model");
            entry["created"]!.GetValue<long>().ShouldBe(0);
            entry["owned_by"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        }

        // First-declaring provider wins for the de-duplicated target.
        data.Single(m => m!["id"]!.GetValue<string>() == "shared-model")!["owned_by"]!
            .GetValue<string>().ShouldBe("opencode-go");

        // NFR-03: the discovery path issues zero upstream requests.
        fixture.Upstream.RequestCount.ShouldBe(upstreamBefore);

        // NFR-04: no provider secret leaks into the body.
        foreach (string secret in ModelsCatalogAppFixture.ConfiguredSecrets)
        {
            body.ShouldNotContain(secret);
        }
    }

    [Fact]
    public async Task Get_openai_models_is_byte_identical_across_calls()
    {
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage first = await client.GetAsync("/openai/v1/models", Ct);
        using HttpResponseMessage second = await client.GetAsync("/openai/v1/models", Ct);

        string firstBody = await first.Content.ReadAsStringAsync(Ct);
        string secondBody = await second.Content.ReadAsStringAsync(Ct);

        secondBody.ShouldBe(firstBody);
    }

    [Fact]
    public async Task Get_anthropic_models_still_passes_through_to_upstream()
    {
        fixture.Upstream.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json")
        };

        HttpClient client = fixture.CreateClient();
        int upstreamBefore = fixture.Upstream.RequestCount;

        using HttpResponseMessage response = await client.GetAsync("/anthropic/v1/models", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // OpenAI-only scope: Anthropic discovery reaches the upstream as a real GET passthrough (LADR-03).
        fixture.Upstream.RequestCount.ShouldBe(upstreamBefore + 1);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://api.anthropic.test/v1/models");
        fixture.Upstream.LastRequestMethod.ShouldBe(HttpMethod.Get);
    }

    [Fact]
    public async Task Post_to_openai_models_still_passes_through_to_upstream()
    {
        fixture.Upstream.ResponseFactory = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json")
        };

        HttpClient client = fixture.CreateClient();
        int upstreamBefore = fixture.Upstream.RequestCount;

        // Non-GET on the OpenAI discovery path falls through to the catch-all passthrough (LADR-03). A
        // body-less POST has no model to match → routed to the OpenAI default, path forwarded verbatim.
        using HttpResponseMessage response = await client.PostAsync("/openai/v1/models", content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        fixture.Upstream.RequestCount.ShouldBe(upstreamBefore + 1);
        fixture.Upstream.LastRequestUri!.ToString().ShouldBe("https://api.openai.test/v1/models");
        fixture.Upstream.LastRequestMethod.ShouldBe(HttpMethod.Post);
    }
}
