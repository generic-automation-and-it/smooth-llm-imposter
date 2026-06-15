using System.Text.Json;
using System.Text.Json.Nodes;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public class RequestTransformerTests
{
    private static RouteDecision Decision(string targetModel, bool caching) =>
        new(
            new ProviderRoute("p", ApiDialect.OpenAi, new Uri("https://p.example"), null, false, null, []),
            targetModel,
            caching,
            IsImposter: true);

    [Fact]
    public void OpenAi_rewrites_model_and_sets_cache_key_when_caching_enabled()
    {
        var transformer = new OpenAiRequestTransformer();
        string body = """{"model":"gpt5.4","messages":[{"role":"user","content":"hi"}]}""";

        JsonNode result = JsonNode.Parse(transformer.Transform(body, Decision("grok-code", caching: true), "gpt5.4"))!;

        result["model"]!.GetValue<string>().ShouldBe("grok-code");
        result["prompt_cache_key"]!.GetValue<string>().ShouldBe("gpt5.4");
    }

    [Fact]
    public void OpenAi_does_not_set_cache_key_when_caching_disabled()
    {
        var transformer = new OpenAiRequestTransformer();
        string body = """{"model":"gpt5.5"}""";

        JsonNode result = JsonNode.Parse(transformer.Transform(body, Decision("gpt5.5", caching: false), "gpt5.5"))!;

        result["model"]!.GetValue<string>().ShouldBe("gpt5.5");
        result.AsObject().ContainsKey("prompt_cache_key").ShouldBeFalse();
    }

    [Fact]
    public void Anthropic_string_system_becomes_block_with_ephemeral_cache_control()
    {
        var transformer = new AnthropicRequestTransformer();
        string body = """{"model":"claude-haiku-x","system":"you are helpful","messages":[{"role":"user","content":"hi"}]}""";

        JsonNode result = JsonNode.Parse(transformer.Transform(body, Decision("claude-3-5-haiku-latest", caching: true), "claude-haiku-x"))!;

        result["model"]!.GetValue<string>().ShouldBe("claude-3-5-haiku-latest");
        JsonArray system = result["system"]!.AsArray();
        system.Count.ShouldBe(1);
        system[0]!["text"]!.GetValue<string>().ShouldBe("you are helpful");
        system[0]!["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
    }

    [Fact]
    public void Anthropic_marks_last_block_of_last_message()
    {
        var transformer = new AnthropicRequestTransformer();
        string body = """
        {"model":"claude-haiku-x","messages":[
          {"role":"user","content":[{"type":"text","text":"first"},{"type":"text","text":"last"}]}
        ]}
        """;

        JsonNode result = JsonNode.Parse(transformer.Transform(body, Decision("target", caching: true), "claude-haiku-x"))!;

        JsonArray content = result["messages"]!.AsArray()[0]!["content"]!.AsArray();
        content[0]!.AsObject().ContainsKey("cache_control").ShouldBeFalse();
        content[1]!["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
    }

    [Fact]
    public void Anthropic_without_caching_only_rewrites_model()
    {
        var transformer = new AnthropicRequestTransformer();
        string body = """{"model":"claude-x","system":"sys","messages":[{"role":"user","content":"hi"}]}""";

        JsonNode result = JsonNode.Parse(transformer.Transform(body, Decision("target", caching: false), "claude-x"))!;

        result["model"]!.GetValue<string>().ShouldBe("target");
        result["system"]!.GetValueKind().ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public void Invalid_json_throws_routing_exception()
    {
        var transformer = new OpenAiRequestTransformer();
        Should.Throw<RoutingException>(() => transformer.Transform("not json", Decision("x", false), "x"));
    }
}
