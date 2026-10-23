using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace SmoothLlmImposter.Host.IntegrationTest;

public sealed class ToolOrderingIntegrationTests
{
    [Theory]
    [InlineData("/v1/messages", "claude-haiku-20241022", true)]
    [InlineData("/anthropic/v1/messages", "claude-haiku-20241022", true)]
    [InlineData("/v1/chat/completions", "gpt5.4", false)]
    [InlineData("/openai/v1/responses", "gpt5.5", false)]
    public async Task Enabled_ordering_appends_new_tools_before_cache_injection_and_preserves_current_catalog(
        string path, string model, bool anthropic)
    {
        await using var fixture = new ImposterAppFixture();
        await using var enabled = fixture.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Imposter:ToolOrdering:Enabled"] = "true" })));
        using HttpClient client = enabled.CreateClient();
        client.DefaultRequestHeaders.Add("x-session-id", "ordering-session");

        async Task<JsonObject> Send(string[] names)
        {
            var tools = new JsonArray();
            foreach (string name in names)
            {
                tools.Add(anthropic
                    ? JsonNode.Parse($$$$"""{"name":"{{{{name}}}}","input_schema":{"type":"object"}}""")
                    : path.EndsWith("responses", StringComparison.Ordinal)
                        ? JsonNode.Parse($$$$"""{"type":"function","name":"{{{{name}}}}","parameters":{"type":"object"}}""")
                        : JsonNode.Parse($$$$"""{"type":"function","function":{"name":"{{{{name}}}}","parameters":{"type":"object"}}}"""));
            }
            var body = new JsonObject { ["model"] = model, ["tools"] = tools, ["system"] = "instructions" };
            body["messages"] = JsonNode.Parse("""[{"role":"user","content":"task"}]""");
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync(path, content, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            return JsonNode.Parse(fixture.Upstream.LastRequestBody!)!.AsObject();
        }

        await Send(["zebra", "alpha"]);
        JsonObject updated = await Send(["new", "alpha", "zebra"]);
        JsonArray tools = updated["tools"]!.AsArray();
        tools.Select(tool => (tool!["function"] ?? tool)["name"]!.GetValue<string>()).ShouldBe(["zebra", "alpha", "new"]);
        if (anthropic)
        {
            tools[0]!.AsObject().ContainsKey("cache_control").ShouldBeFalse();
            tools[^1]!["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
        }
        JsonObject removed = await Send(["new"]);
        removed["tools"]!.AsArray().Count.ShouldBe(1);
        (removed["tools"]![0]!["function"] ?? removed["tools"]![0])!["name"]!.GetValue<string>().ShouldBe("new");
    }
}
