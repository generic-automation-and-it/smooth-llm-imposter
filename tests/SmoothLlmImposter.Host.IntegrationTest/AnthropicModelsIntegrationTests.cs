using System.Net;
using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Host.IntegrationTest;

/// <summary>
/// L2 coverage for the locally-synthesized <c>GET /anthropic/v1/models</c> aggregation (HLD 005, Anthropic
/// scope). Every test here serves the endpoint from configuration, so the stub upstream must stay untouched —
/// no test in this class issues an outbound call, which lets the no-upstream-call assertion (NFR-03) hold
/// regardless of test execution order.
/// </summary>
public sealed class AnthropicModelsIntegrationTests(AnthropicModelsAppFixture fixture) : IClassFixture<AnthropicModelsAppFixture>
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Get_anthropic_models_returns_distinct_union_synthesized_locally_without_upstream_call()
    {
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/anthropic/v1/models", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

        JsonObject envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();

        string[] ids = [.. envelope["data"]!.AsArray().Select(m => m!["id"]!.GetValue<string>())];
        // Distinct union across the two Anthropic imposters, dedup of claude-sonnet-4-6, OpenAI target excluded.
        ids.ShouldBe(["claude-sonnet-4-6", "claude-opus-4-8", "claude-haiku-4-5"]);
        ids.ShouldNotContain("grok-code");

        // Anthropic envelope shape (no object:"list"; pagination fields present).
        envelope.ContainsKey("object").ShouldBeFalse();
        envelope["has_more"]!.GetValue<bool>().ShouldBeFalse();
        envelope["first_id"]!.GetValue<string>().ShouldBe("claude-sonnet-4-6");
        envelope["last_id"]!.GetValue<string>().ShouldBe("claude-haiku-4-5");

        JsonObject first = envelope["data"]!.AsArray()[0]!.AsObject();
        first["type"]!.GetValue<string>().ShouldBe("model");
        first["display_name"]!.GetValue<string>().ShouldBe("claude-sonnet-4-6");
        first["created_at"]!.GetValue<string>().ShouldBe("1970-01-01T00:00:00Z");

        // NFR-03: answered from config alone — the upstream transport was never invoked.
        fixture.Upstream.LastRequestUri.ShouldBeNull();
    }

    [Fact]
    public async Task Get_anthropic_models_is_deterministic_across_calls()
    {
        HttpClient client = fixture.CreateClient();

        using HttpResponseMessage first = await client.GetAsync("/anthropic/v1/models", Ct);
        using HttpResponseMessage second = await client.GetAsync("/anthropic/v1/models", Ct);

        string firstBody = await first.Content.ReadAsStringAsync(Ct);
        string secondBody = await second.Content.ReadAsStringAsync(Ct);
        secondBody.ShouldBe(firstBody);

        fixture.Upstream.LastRequestUri.ShouldBeNull();
    }
}
