using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public sealed class ToolCatalogOrdererTests
{
    [Fact]
    public void First_seen_order_survives_reordering_and_new_tools_append_without_restoring_removed_tools()
    {
        ToolCatalogOrderer orderer = Create();
        Order(orderer, "session", "zebra", "alpha").ShouldBe(["zebra", "alpha"]);

        Order(orderer, "session", "new", "alpha", "zebra").ShouldBe(["zebra", "alpha", "new"]);
        Order(orderer, "session", "new", "alpha").ShouldBe(["alpha", "new"]);
        Order(orderer, "session", "new", "alpha", "zebra").ShouldBe(["zebra", "alpha", "new"]);
    }

    [Theory]
    [InlineData("/v1/chat/completions")]
    [InlineData("/v1/responses")]
    public void Openai_flat_and_nested_function_tools_have_stable_order(string path)
    {
        ToolCatalogOrderer orderer = Create();
        string Build(string[] names)
        {
            var tools = new JsonArray();
            foreach (string name in names)
            {
                tools.Add(JsonNode.Parse(path.EndsWith("responses", StringComparison.Ordinal)
                    ? $$$$"""{"type":"function","name":"{{{{name}}}}","parameters":{}}"""
                    : $$$$"""{"type":"function","function":{"name":"{{{{name}}}}","parameters":{}}}"""));
            }
            return new JsonObject { ["model"] = "inbound", ["tools"] = tools }.ToJsonString();
        }
        orderer.Order(ApiDialect.OpenAi, path, Build(["zebra", "alpha"]), Headers("session"));

        JsonArray tools = JsonNode.Parse(orderer.Order(ApiDialect.OpenAi, path, Build(["new", "alpha", "zebra"]), Headers("session")))!["tools"]!.AsArray();

        tools.Select(tool => (tool!["function"] ?? tool)["name"]!.GetValue<string>()).ShouldBe(["zebra", "alpha", "new"]);
    }

    [Fact]
    public void Disabled_ordering_and_missing_or_derived_session_identity_preserve_original_body_instance()
    {
        var options = new ImposterOptions { ToolOrdering = new ToolOrderingOptions { Enabled = true } };
        var disabled = new ToolCatalogOrderer(Options.Create(options), TimeProvider.System);
        Order(disabled, "session", "zebra", "alpha");
        options.ToolOrdering.Enabled = false;
        string body = Body("alpha", "zebra");
        disabled.Order(ApiDialect.Anthropic, "/v1/messages", body, Headers("session")).ShouldBeSameAs(body);
        ToolCatalogOrderer orderer = Create();
        orderer.Order(ApiDialect.Anthropic, "/v1/messages", Body("zebra", "alpha"), CallerHeaders.None);
        orderer.Order(ApiDialect.Anthropic, "/v1/messages", body, CallerHeaders.None).ShouldBeSameAs(body);
        var authenticationOnly = new CallerHeaders([new("authorization", new[] { "Bearer caller" })]);
        SessionIdentity derived = SessionIdentityResolver.Resolve(authenticationOnly, body);
        derived.Source.ShouldBe(SessionIdentitySource.Derived);
        orderer.Order(ApiDialect.Anthropic, "/v1/messages", Body("zebra", "alpha"), Headers(derived.Value!, "Bearer caller"));
        orderer.Order(ApiDialect.Anthropic, "/v1/messages", body, authenticationOnly).ShouldBeSameAs(body);
    }

    [Fact]
    public void Session_model_dialect_and_caller_authentication_have_independent_rank_scopes()
    {
        ToolCatalogOrderer orderer = Create();
        Order(orderer, "one", "zebra", "alpha");
        Order(orderer, "two", "alpha", "zebra").ShouldBe(["alpha", "zebra"]);
        string reversed = Body("alpha", "zebra");
        orderer.Order(ApiDialect.Anthropic, "/v1/messages", reversed, Headers("one", "other-caller")).ShouldBeSameAs(reversed);
        string otherModel = reversed.Replace("inbound", "other-model", StringComparison.Ordinal);
        orderer.Order(ApiDialect.Anthropic, "/v1/messages", otherModel, Headers("one")).ShouldBeSameAs(otherModel);
        string openai = """{"model":"inbound","tools":[{"type":"function","name":"alpha"},{"type":"function","name":"zebra"}]}""";
        orderer.Order(ApiDialect.OpenAi, "/v1/responses", openai, Headers("one")).ShouldBeSameAs(openai);
        Order(orderer, "one", "alpha", "zebra").ShouldBe(["zebra", "alpha"]);
    }

    [Theory]
    [InlineData("""{"tools":[{"name":"alpha","input_schema":{},"defer_loading":true},{"name":"zebra","input_schema":{}}]}""")]
    [InlineData("""{"tools":[{"name":"alpha","input_schema":{},"cache_control":{"type":"ephemeral"}},{"name":"zebra","input_schema":{}}]}""")]
    [InlineData("""{"cache_control":{"type":"ephemeral"},"tools":[{"name":"alpha","input_schema":{}},{"name":"zebra","input_schema":{}}]}""")]
    [InlineData("""{"system":[{"cache_control":{"type":"ephemeral"}}],"tools":[{"name":"alpha","input_schema":{}},{"name":"zebra","input_schema":{}}]}""")]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"text","text":"task","cache_control":{"type":"ephemeral"}}]}],"tools":[{"name":"alpha","input_schema":{}},{"name":"zebra","input_schema":{}}]}""")]
    [InlineData("""{"tools":[{"name":"alpha","input_schema":{}},{"name":"zebra","input_schema":{}},{"name":"alpha","input_schema":{}}]}""")]
    [InlineData("""{"tools":[{"type":"unknown"},{"name":"zebra","input_schema":{}}]}""")]
    [InlineData("""{"tools":[{"name":"alpha"},{"name":"zebra","input_schema":{}}]}""")]
    [InlineData("""{"tools":[null,{"name":"zebra","input_schema":{}}]}""")]
    [InlineData("""{"tools":[{"name":"alpha","input_schema":{},"prompt_cache_breakpoint":true},{"name":"zebra","input_schema":{}}]}""")]
    public void Caller_managed_deferred_or_ambiguous_catalogs_are_not_reordered(string body)
    {
        ToolCatalogOrderer orderer = Create();
        Order(orderer, "session", "zebra", "alpha");
        JsonObject request = JsonNode.Parse(body)!.AsObject();
        request["model"] = "inbound";
        body = request.ToJsonString();

        orderer.Order(ApiDialect.Anthropic, "/v1/messages", body, Headers("session")).ShouldBeSameAs(body);
    }

    [Theory]
    [InlineData("/v1/models")]
    [InlineData("/v1/messages/count_tokens")]
    [InlineData("/v1/responses")]
    public void Unrelated_paths_and_wrong_dialect_are_noops(string path)
    {
        string body = Body("zebra", "alpha");

        Create().Order(ApiDialect.Anthropic, path, body, Headers("session")).ShouldBeSameAs(body);
    }

    [Theory]
    [InlineData("previous_response_id")]
    [InlineData("conversation")]
    [InlineData("prompt_cache_options")]
    public void Responses_managed_state_or_caching_is_not_reordered(string field)
    {
        ToolCatalogOrderer orderer = Create();
        orderer.Order(ApiDialect.OpenAi, "/v1/responses", ResponsesBody("zebra", "alpha"), Headers("session"));
        JsonObject request = JsonNode.Parse(ResponsesBody("alpha", "zebra"))!.AsObject();
        request[field] = "caller-managed";
        string body = request.ToJsonString();

        orderer.Order(ApiDialect.OpenAi, "/v1/responses", body, Headers("session")).ShouldBeSameAs(body);
    }

    [Fact]
    public void Responses_explicit_text_cache_breakpoint_preserves_caller_order_without_options()
    {
        ToolCatalogOrderer orderer = Create();
        orderer.Order(ApiDialect.OpenAi, "/v1/responses", ResponsesBody("zebra", "alpha"), Headers("session"));
        JsonObject request = JsonNode.Parse(ResponsesBody("alpha", "zebra"))!.AsObject();
        request["input"] = JsonNode.Parse("""
        [{"role":"user","content":[{"type":"input_text","text":"instructions",
          "prompt_cache_breakpoint":{"mode":"explicit"}}]}]
        """);
        string body = request.ToJsonString();

        orderer.Order(ApiDialect.OpenAi, "/v1/responses", body, Headers("session")).ShouldBeSameAs(body);
    }

    [Fact]
    public void Schema_property_named_prompt_cache_breakpoint_does_not_disable_ordering()
    {
        ToolCatalogOrderer orderer = Create();
        orderer.Order(ApiDialect.OpenAi, "/v1/responses", ResponsesBody("zebra", "alpha"), Headers("session"));
        JsonObject request = JsonNode.Parse(ResponsesBody("alpha", "zebra"))!.AsObject();
        request["tools"]![0]!["parameters"] = JsonNode.Parse("""
        {"type":"object","properties":{"prompt_cache_breakpoint":{"type":"string"}}}
        """);

        JsonObject result = JsonNode.Parse(orderer.Order(ApiDialect.OpenAi, "/v1/responses",
            request.ToJsonString(), Headers("session")))!.AsObject();

        result["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ShouldBe(["zebra", "alpha"]);
        JsonNode.DeepEquals(result["tools"]![1], request["tools"]![0]).ShouldBeTrue();
    }

    [Fact]
    public void Reordering_preserves_definition_updates_choice_history_and_non_tool_bytes()
    {
        ToolCatalogOrderer orderer = Create();
        Order(orderer, "session", "zebra", "alpha");
        const string body = """
        { "model" : "inbound", "tools" : [{"name":"alpha","description":"updated","input_schema":{"type":"object"}},
          {"name":"zebra","input_schema":{}}], "tool_choice" : {"type":"tool","name":"alpha"},
          "messages" : [{"role":"assistant","content":[{"type":"tool_use","name":"alpha","id":"one","input":{}}]}],
          "metadata" : { "html" : "<literal>", "number" : 1.00 } }
        """;

        string result = orderer.Order(ApiDialect.Anthropic, "/v1/messages", body, Headers("session"));

        result.ShouldContain("\"metadata\" : { \"html\" : \"<literal>\", \"number\" : 1.00 }");
        JsonObject original = JsonNode.Parse(body)!.AsObject();
        JsonObject reordered = JsonNode.Parse(result)!.AsObject();
        JsonNode.DeepEquals(original["tools"]![0], reordered["tools"]![1]).ShouldBeTrue();
        JsonNode.DeepEquals(original["tools"]![1], reordered["tools"]![0]).ShouldBeTrue();
        JsonNode.DeepEquals(original["messages"], reordered["messages"]).ShouldBeTrue();
        JsonNode.DeepEquals(original["tool_choice"], reordered["tool_choice"]).ShouldBeTrue();
        orderer.Order(ApiDialect.Anthropic, "/v1/messages", result, Headers("session")).ShouldBeSameAs(result);
    }

    [Fact]
    public void Idle_expiry_resets_first_seen_order()
    {
        var clock = new ManualClock();
        ToolCatalogOrderer orderer = Create(clock: clock);
        Order(orderer, "session", "zebra", "alpha");

        clock.Advance(SessionScopedMemory<object>.IdleLifetime);

        Order(orderer, "session", "alpha", "zebra").ShouldBe(["alpha", "zebra"]);
    }

    [Fact]
    public void Tool_rank_capacity_fails_open_without_losing_existing_order()
    {
        ToolCatalogOrderer orderer = Create();
        string[] names = Enumerable.Range(0, ToolCatalogOrderer.MaxTools).Select(index => $"tool-{index}").ToArray();
        Order(orderer, "session", names);
        string overflow = Body("new", names[^1], names[0]);

        orderer.Order(ApiDialect.Anthropic, "/v1/messages", overflow, Headers("session")).ShouldBeSameAs(overflow);
        Order(orderer, "session", names[^1], names[0]).ShouldBe([names[0], names[^1]]);
    }

    [Fact]
    public void Rank_saturated_scope_with_recent_overflow_traffic_is_not_idle()
    {
        var clock = new ManualClock();
        ToolCatalogOrderer orderer = Create(clock: clock);
        string[] names = Enumerable.Range(0, ToolCatalogOrderer.MaxTools).Select(index => $"tool-{index}").ToArray();
        Order(orderer, "session", names);
        clock.Advance(TimeSpan.FromMinutes(29));
        string overflow = Body("new", names[^1], names[0]);

        orderer.Order(ApiDialect.Anthropic, "/v1/messages", overflow, Headers("session")).ShouldBeSameAs(overflow);
        clock.Advance(TimeSpan.FromMinutes(2));

        Order(orderer, "session", names[^1], names[0]).ShouldBe([names[0], names[^1]]);
    }

    [Fact]
    public async Task Concurrent_requests_preserve_seeded_ranks_and_current_definitions()
    {
        ToolCatalogOrderer orderer = Create();
        Order(orderer, "session", "zebra", "alpha");

        string[][] results = await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            Task.Run(() => Order(orderer, "session", $"new-{index}", "alpha", "zebra"))));

        for (int index = 0; index < results.Length; index++)
        {
            results[index].ShouldBe(["zebra", "alpha", $"new-{index}"]);
        }
    }

    private static ToolCatalogOrderer Create(bool enabled = true, TimeProvider? clock = null) =>
        new(Options.Create(new ImposterOptions { ToolOrdering = new ToolOrderingOptions { Enabled = enabled } }), clock ?? TimeProvider.System);

    private static CallerHeaders Headers(string session, string authentication = "caller") =>
        new([new("x-session-id", new[] { session }), new("authorization", new[] { authentication })]);

    private static string Body(params string[] names)
    {
        var tools = new JsonArray();
        foreach (string name in names)
        {
            tools.Add((JsonNode)new JsonObject { ["name"] = name, ["input_schema"] = new JsonObject() });
        }
        return new JsonObject { ["model"] = "inbound", ["tools"] = tools }.ToJsonString();
    }

    private static string[] Order(ToolCatalogOrderer orderer, string session, params string[] names) =>
        JsonNode.Parse(orderer.Order(ApiDialect.Anthropic, "/v1/messages", Body(names), Headers(session)))!["tools"]!
            .AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToArray();

    private static string ResponsesBody(params string[] names)
    {
        var tools = new JsonArray();
        foreach (string name in names)
        {
            tools.Add((JsonNode)new JsonObject { ["type"] = "function", ["name"] = name, ["parameters"] = new JsonObject() });
        }
        return new JsonObject { ["model"] = "inbound", ["tools"] = tools }.ToJsonString();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan interval) => _now += interval;
    }
}
