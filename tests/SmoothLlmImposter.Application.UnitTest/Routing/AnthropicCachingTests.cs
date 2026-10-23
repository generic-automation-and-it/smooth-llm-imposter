using System.Text.Json.Nodes;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public sealed class AnthropicCachingTests
{
    [Fact]
    public void Unmarked_request_gets_tools_system_and_message_breakpoints_without_reordering_tools()
    {
        const string body = """
        {"model":"inbound","system":"instructions","messages":[{"role":"user","content":"task"}],
         "tools":[{"name":"zebra","input_schema":{"type":"object"}},
                  {"name":"alpha","description":"lookup","input_schema":{"type":"object"}}],
         "tool_choice":{"type":"tool","name":"alpha"}}
        """;

        JsonObject result = Transform(body);

        result["model"]!.GetValue<string>().ShouldBe("target");
        JsonArray tools = result["tools"]!.AsArray();
        tools.Select(tool => tool!["name"]!.GetValue<string>()).ShouldBe(["zebra", "alpha"]);
        tools[0]!.AsObject().ContainsKey("cache_control").ShouldBeFalse();
        tools[1]!["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
        result["system"]![0]!["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
        result["messages"]![0]!["content"]![0]!["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
        tools[1]!.AsObject().Remove("cache_control");
        JsonObject original = JsonNode.Parse(body)!.AsObject();
        JsonNode.DeepEquals(tools, original["tools"]).ShouldBeTrue();
        JsonNode.DeepEquals(result["tool_choice"], original["tool_choice"]).ShouldBeTrue();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"tools\":null}")]
    [InlineData("{\"tools\":[]}")]
    [InlineData("{\"tools\":[null]}")]
    public void Missing_or_empty_tools_do_not_prevent_message_caching(string body)
    {
        JsonObject request = JsonNode.Parse(body)!.AsObject();
        request["model"] = "inbound";
        request["messages"] = JsonNode.Parse("""[{"role":"user","content":"task"}]""");

        JsonObject result = Transform(request.ToJsonString());

        JsonNode.DeepEquals(result["tools"], request["tools"]).ShouldBeTrue();
        result["messages"]![0]!["content"]![0]!["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
    }

    [Theory]
    [InlineData("""{"cache_control":{"type":"ephemeral","ttl":"1h"}}""")]
    [InlineData("""{"tools":[{"name":"lookup","input_schema":{},"cache_control":{"type":"ephemeral","ttl":"1h"}}]}""")]
    [InlineData("""{"system":[{"type":"text","text":"instructions","cache_control":{"type":"ephemeral","ttl":"1h"}}]}""")]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"text","text":"task","cache_control":{"type":"ephemeral","ttl":"1h"}}]}]}""")]
    [InlineData("""
        {"tools":[{"name":"lookup","input_schema":{},"cache_control":{"type":"ephemeral","ttl":"1h"}}],
         "system":[{"type":"text","text":"one","cache_control":{"type":"ephemeral","ttl":"1h"}},
                   {"type":"text","text":"two","cache_control":{"type":"ephemeral"}}],
         "messages":[{"role":"user","content":[{"type":"text","text":"task","cache_control":{"type":"ephemeral"}}]}]}
        """)]
    public void Caller_caching_layout_is_preserved_without_extra_breakpoints(string body)
    {
        JsonObject original = JsonNode.Parse(body)!.AsObject();
        original["model"] = "inbound";
        if (!original.ContainsKey("tools"))
        {
            original["tools"] = JsonNode.Parse("""[{"name":"lookup","input_schema":{}}]""");
        }
        if (!original.ContainsKey("system"))
        {
            original["system"] = "instructions";
        }
        if (!original.ContainsKey("messages"))
        {
            original["messages"] = JsonNode.Parse("""[{"role":"user","content":"task"}]""");
        }

        JsonObject result = Transform(original.ToJsonString());

        original["model"] = "target";
        JsonNode.DeepEquals(result, original).ShouldBeTrue();
    }

    [Fact]
    public void Schema_property_named_cache_control_is_not_a_caller_breakpoint()
    {
        const string body = """
        {"model":"inbound","tools":[{"name":"lookup","input_schema":{
          "type":"object","properties":{"cache_control":{"type":"string"}}}}]}
        """;

        JsonObject result = Transform(body);

        result["tools"]![0]!["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
        result["tools"]![0]!["input_schema"]!["properties"]!["cache_control"]!["type"]!.GetValue<string>().ShouldBe("string");
    }

    [Fact]
    public void Tool_additions_and_removals_follow_the_current_harness_catalog()
    {
        foreach (string[] names in new[] { new[] { "zebra", "alpha" }, new[] { "zebra", "alpha", "new" }, new[] { "new" } })
        {
            var tools = new JsonArray();
            foreach (string name in names)
            {
                tools.Add((JsonNode)new JsonObject { ["name"] = name, ["input_schema"] = new JsonObject() });
            }
            var request = new JsonObject { ["model"] = "inbound", ["tools"] = tools };

            JsonObject result = Transform(request.ToJsonString());

            result["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ShouldBe(names);
        }
    }

    [Fact]
    public void Repeated_transformation_is_idempotent()
    {
        const string body = """
        {"model":"inbound","system":"instructions","messages":[{"role":"user","content":"task"}],
         "tools":[{"name":"lookup","input_schema":{}}]}
        """;
        JsonObject first = Transform(body);

        JsonNode.DeepEquals(Transform(first.ToJsonString()), first).ShouldBeTrue();
    }

    [Fact]
    public void Disabled_caching_leaves_tools_and_caller_cache_controls_untouched()
    {
        const string body = """
        {"model":"inbound","tools":[{"name":"lookup","input_schema":{}}],
         "system":[{"type":"text","text":"instructions","cache_control":{"type":"ephemeral","ttl":"1h"}}]}
        """;
        JsonObject original = JsonNode.Parse(body)!.AsObject();

        JsonObject result = Transform(body, caching: false);

        original["model"] = "target";
        JsonNode.DeepEquals(result, original).ShouldBeTrue();
    }

    private static JsonObject Transform(string body, bool caching = true)
    {
        var provider = new ProviderRoute("p", ApiDialect.Anthropic, new Uri("https://p.example"), null, false, null, []);
        var decision = new RouteDecision(provider, "target", caching, IsImposter: true);
        string transformed = new AnthropicRequestTransformer().Transform(body, decision, "inbound", SessionIdentity.None);
        return JsonNode.Parse(transformed)!.AsObject();
    }
}
