using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.Features.Routing.Decisions;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Decisions;

public sealed class DecisionToolsFilterTests
{
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Disabled_malformed_body_is_not_parsed_and_logs_debug_only()
    {
        var client = new DecisionStub();
        var logs = new FilterLogger();
        var filter = Create(client, logs, enabled: false);
        string body = new("not JSON".ToCharArray());

        ReferenceEquals(await filter.FilterAsync("/v1/messages", body, Ct), body).ShouldBeTrue();
        client.Requests.ShouldBeEmpty();
        logs.Events.Single().Level.ShouldBe(LogLevel.Debug);
    }

    [Theory]
    [InlineData("/v1/messages/count_tokens", "not JSON")]
    [InlineData("/v1/models", "not JSON")]
    [InlineData("/v1/chat/completions", "")]
    [InlineData("/v1/chat/completions", "{}")]
    [InlineData("/v1/chat/completions", "{\"tools\":[]}")]
    [InlineData("/v1/responses", "{\"tools\":[{\"type\":\"web_search\"}],\"input\":\"Hi\",\"previous_response_id\":\"resp_1\"}")]
    [InlineData("/v1/responses", "{\"tools\":[{\"type\":\"web_search\"}],\"input\":\"Hi\",\"conversation\":{\"id\":\"conv_1\"}}")]
    [InlineData("/v1/responses", "{\"tools\":[{\"type\":\"web_search\"}],\"messages\":[{\"role\":\"user\",\"content\":\"Hi\"}]}")]
    [InlineData("/v1/messages", "{\"tools\":[{\"name\":\"f\",\"input_schema\":{}}],\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"content\":\"ignored\"}]}]}")]
    public async Task Skips_preserve_original_instance_without_calls(string path, string body)
    {
        var client = new DecisionStub();
        ReferenceEquals(await Create(client).FilterAsync(path, body, Ct), body).ShouldBeTrue();
        client.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Chat_golden_protects_function_and_custom_history_and_prunes_allowed_tools()
    {
        string body = """
            {"messages":[
              {"role":"system","content":"System"},
              {"role":"user","content":"Earlier"},
              {"role":"assistant","content":null,"tool_calls":[{"type":"function","function":{"name":"used"}},{"type":"custom","custom":{"name":"patch"}}]},
              {"role":"tool","content":"Ignored output"},
              {"role":"user","content":[{"type":"text","text":"Latest"},{"type":"image_url","image_url":{"url":"ignore"}}]}],
             "tools":[{"type":"function","function":{"name":"used"}},{"type":"custom","custom":{"name":"patch"}},
              {"type":"function","function":{"name":"unused","description":"Tool description"}},{"type":"future","name":"opaque"}],
             "tool_choice":{"type":"allowed_tools","allowed_tools":{"mode":"auto","tools":[{"type":"function","function":{"name":"used"}},{"type":"function","function":{"name":"unused"}}]}},
             "functions":[{"name":"legacy"}],"function_call":{"name":"legacy"}}
            """;
        var client = new DecisionStub();
        JsonObject filtered = JsonNode.Parse(await Create(client).FilterAsync("/v1/chat/completions", body, Ct))!.AsObject();

        filtered["tools"]!.AsArray().Select(node => node?["type"]?.GetValue<string>()).ToArray()
            .ShouldBe(["function", "custom", "future"]);
        filtered["messages"]!.ToJsonString().ShouldBe(JsonNode.Parse(body)!["messages"]!.ToJsonString());
        filtered["functions"]!.ToJsonString().ShouldBe("[{\"name\":\"legacy\"}]");
        filtered["function_call"]!["name"]!.GetValue<string>().ShouldBe("legacy");
        filtered["tool_choice"]!["allowed_tools"]!["tools"]!.AsArray().Count.ShouldBe(1);
        JsonObject request = client.Requests.Single();
        request["state"]!.GetValue<string>().ShouldBe("Latest\nEarlier\nSystem");
        request["questions"]!.AsObject().Select(pair => pair.Key).ToArray().ShouldBe(["t2"]);
        request["questions"]!["t2"]!["instructions"]!.GetValue<string>().ShouldBe("unused Tool description");
        request["questions"]!["t2"]!["criteria"]!.ToJsonString()
            .ShouldBe("{\"use\":\"the assistant will call this tool to answer\",\"skip\":\"not needed\"}");
    }

    [Fact]
    public async Task Responses_golden_protects_namespace_mcp_hosted_history_and_ignores_non_messages()
    {
        string body = """
            {"instructions":"System","input":[
              {"type":"message","role":"user","content":[{"type":"input_text","text":"Earlier"}]},
              {"type":"function_call","name":"child"},
              {"type":"custom_tool_call","name":"patch","namespace":"ns2"},
              {"type":"mcp_call","name":"remote","server_label":"one"},
              {"type":"web_search_call"},
              {"type":"message","role":"assistant","content":[{"type":"output_text","text":"Earlier answer"}]},
              {"type":"message","role":"user","content":"Latest"},
              {"type":"function_call_output","output":"Ignored output"},
              {"type":"reasoning","text":"Ignored reasoning"}],
             "tools":[{"type":"namespace","name":"ns","tools":[{"type":"function","name":"child"}]},
              {"type":"namespace","name":"ns2","tools":[{"type":"custom","name":"other"}]},
              {"type":"mcp","server_label":"one"},{"type":"web_search"},{"type":"web_search_preview"},
              {"type":"mcp","server_label":"two"},{"type":"custom","name":"unused"},
              {"type":"image_generation"},{"type":"tool_search"},{"type":"local_shell"},{"type":"file_search"},
              {"type":"future","name":"opaque"}],
             "tool_choice":{"type":"allowed_tools","mode":"auto","tools":[{"type":"web_search"},{"type":"custom","name":"unused"}]}}
            """;
        var client = new DecisionStub();
        JsonObject result = JsonNode.Parse(await Create(client).FilterAsync("/responses", body, Ct))!.AsObject();
        result["tools"]!.AsArray().Count.ShouldBe(6);
        result["tool_choice"]!["tools"]!.AsArray().Count.ShouldBe(1);
        result["input"]!.ToJsonString().ShouldBe(JsonNode.Parse(body)!["input"]!.ToJsonString());
        JsonObject request = client.Requests.Single();
        request["state"]!.GetValue<string>().ShouldBe("Latest\nEarlier answer\nEarlier\nSystem");
        request["questions"]!.AsObject().Select(pair => pair.Key).ToArray()
            .ShouldBe(["t5", "t6", "t7", "t8", "t9", "t10"]);
        request["questions"]!["t5"]!["instructions"]!.GetValue<string>().ShouldBe("two");
    }

    [Fact]
    public async Task Anthropic_golden_protects_all_history_blocks_and_preserves_mcp_servers()
    {
        string body = """
            {"system":[{"type":"text","text":"System"}],"messages":[
              {"role":"user","content":"Earlier"},
              {"role":"assistant","content":[{"type":"tool_use","name":"local"},{"type":"server_tool_use","name":"web_search"},{"type":"mcp_tool_use","name":"remote"}]},
              {"role":"user","content":[{"type":"text","text":"Latest"},{"type":"image","source":{}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"x","content":"Ignored"}]}],
             "tools":[{"name":"local","input_schema":{}},{"type":"web_search_20250305","name":"web_search"},
              {"name":"remote","input_schema":{}},{"name":"unused","input_schema":{}},{"type":"future","name":"opaque","input_schema":{}}],
             "tool_choice":{"type":"tool","name":"unused"},"mcp_servers":[{"name":"connector"}]}
            """;
        var client = new DecisionStub();
        JsonObject result = JsonNode.Parse(await Create(client).FilterAsync("/v1/messages", body, Ct))!.AsObject();
        result["tools"]!.AsArray().Count.ShouldBe(4);
        result.ContainsKey("tool_choice").ShouldBeFalse();
        result["messages"]!.ToJsonString().ShouldBe(JsonNode.Parse(body)!["messages"]!.ToJsonString());
        result["mcp_servers"]!.ToJsonString().ShouldBe("[{\"name\":\"connector\"}]");
        client.Requests.Single()["state"]!.GetValue<string>().ShouldBe("Latest\nEarlier\nSystem");
        client.Requests.Single()["questions"]!.AsObject().Select(pair => pair.Key).ToArray().ShouldBe(["t3"]);
    }

    [Theory]
    [InlineData("/chat/completions", "{\"type\":\"custom\",\"custom\":{\"name\":\"drop\"}}", "{\"type\":\"custom\",\"custom\":{\"name\":\"keep\"}}", "{\"type\":\"custom\",\"custom\":{\"name\":\"drop\"}}")]
    [InlineData("/chat/completions", "{\"type\":\"function\",\"function\":{\"name\":\"drop\"}}", "{\"type\":\"function\",\"function\":{\"name\":\"keep\"}}", "{\"type\":\"function\",\"function\":{\"name\":\"drop\"}}")]
    [InlineData("/responses", "{\"type\":\"function\",\"name\":\"drop\"}", "{\"type\":\"function\",\"name\":\"keep\"}", "{\"type\":\"function\",\"name\":\"drop\"}")]
    [InlineData("/responses", "{\"type\":\"custom\",\"name\":\"drop\"}", "{\"type\":\"custom\",\"name\":\"keep\"}", "{\"type\":\"custom\",\"name\":\"drop\"}")]
    [InlineData("/messages", "{\"name\":\"drop\",\"input_schema\":{}}", "{\"name\":\"keep\",\"input_schema\":{}}", "{\"type\":\"tool\",\"name\":\"drop\"}")]
    public async Task Dangling_named_choice_is_removed(string path, string drop, string keep, string choice)
    {
        string body = $$"""{"input":"Task","messages":[{"role":"user","content":"Task"}],"tools":[{{drop}},{{keep}}],"tool_choice":{{choice}}}""";
        var client = new DecisionStub { Answer = request => Answers(request, key => key == "t0" ? 0 : 1) };
        JsonObject result = JsonNode.Parse(await Create(client).FilterAsync(path, body, Ct))!.AsObject();
        result["tools"]!.AsArray().Count.ShouldBe(1);
        result.ContainsKey("tool_choice").ShouldBeFalse();
    }

    [Theory]
    [InlineData("\"required\"")]
    [InlineData("{\"type\":\"future\",\"name\":\"drop\"}")]
    [InlineData("{\"type\":\"auto\"}")]
    [InlineData("{\"type\":\"web_search\"}")]
    public async Task String_and_unknown_choice_forms_are_preserved(string choice)
    {
        JsonObject body = Body(2);
        body["tool_choice"] = JsonNode.Parse(choice);
        var client = new DecisionStub { Answer = request => Answers(request, key => key == "t0" ? 0 : 1) };
        JsonObject result = JsonNode.Parse(await Create(client).FilterAsync("/responses", body.ToJsonString(), Ct))!.AsObject();
        result["tool_choice"]!.ToJsonString().ShouldBe(JsonNode.Parse(choice)!.ToJsonString());
    }

    [Fact]
    public async Task None_survive_removes_tools_and_any_choice()
    {
        JsonObject body = Body(2);
        body["tool_choice"] = "required";
        JsonObject result = JsonNode.Parse(await Create(new DecisionStub()).FilterAsync("/responses", body.ToJsonString(), Ct))!.AsObject();
        result.ContainsKey("tools").ShouldBeFalse();
        result.ContainsKey("tool_choice").ShouldBeFalse();
    }

    [Theory]
    [InlineData("/chat/completions", "{\"type\":\"allowed_tools\",\"allowed_tools\":{\"mode\":\"required\",\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"drop\"}}]}}", "{\"type\":\"function\",\"function\":{\"name\":\"drop\"}}", "{\"type\":\"function\",\"function\":{\"name\":\"keep\"}}")]
    [InlineData("/responses", "{\"type\":\"allowed_tools\",\"mode\":\"required\",\"tools\":[{\"type\":\"function\",\"name\":\"drop\"}]}", "{\"type\":\"function\",\"name\":\"drop\"}", "{\"type\":\"function\",\"name\":\"keep\"}")]
    public async Task Empty_allowed_tools_choice_is_removed_while_other_tools_survive(string path, string choice, string drop, string keep)
    {
        string body = $$"""{"input":"Task","messages":[{"role":"user","content":"Task"}],"tools":[{{drop}},{{keep}}],"tool_choice":{{choice}}}""";
        var client = new DecisionStub { Answer = request => Answers(request, key => key == "t0" ? 0 : 1) };
        JsonObject result = JsonNode.Parse(await Create(client).FilterAsync(path, body, Ct))!.AsObject();
        result["tools"]!.AsArray().Count.ShouldBe(1);
        result.ContainsKey("tool_choice").ShouldBeFalse();
    }

    [Fact]
    public async Task Flat_responses_history_preserves_named_function_custom_and_mcp_tools()
    {
        string body = """
            {"input":[{"type":"message","role":"user","content":"Task"},
             {"type":"function_call","name":"f"},{"type":"custom_tool_call","name":"patch"},
             {"type":"mcp_call","name":"remote"}],
             "tools":[{"type":"function","name":"f"},{"type":"custom","name":"patch"},
             {"type":"mcp","server_label":"one","name":"remote"}]}
            """;
        var client = new DecisionStub();
        ReferenceEquals(await Create(client).FilterAsync("/responses", body, Ct), body).ShouldBeTrue();
        client.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Choice_for_child_of_surviving_namespace_is_not_dangling()
    {
        string body = """
            {"input":"Task","tools":[{"type":"function","name":"drop"},
             {"type":"namespace","name":"ns","tools":[{"type":"function","name":"child"}]}],
             "tool_choice":{"type":"function","name":"child","namespace":"ns"}}
            """;
        var client = new DecisionStub { Answer = request => Answers(request, key => key == "t0" ? 0 : 1) };
        JsonObject result = JsonNode.Parse(await Create(client).FilterAsync("/responses", body, Ct))!.AsObject();
        result["tools"]!.AsArray().Count.ShouldBe(1);
        result["tool_choice"]!["name"]!.GetValue<string>().ShouldBe("child");
    }

    [Fact]
    public async Task Same_named_flat_tool_cannot_keep_a_removed_namespaced_choice()
    {
        string body = """
            {"input":"Task","tools":[{"type":"namespace","name":"ns","tools":[{"type":"function","name":"child"}]},
             {"type":"function","name":"child"}],"tool_choice":{"type":"function","name":"child","namespace":"ns"}}
            """;
        var client = new DecisionStub { Answer = request => Answers(request, key => key == "t0" ? 0 : 1) };
        JsonObject result = JsonNode.Parse(await Create(client).FilterAsync("/responses", body, Ct))!.AsObject();
        result["tools"]!.AsArray().Count.ShouldBe(1);
        result.ContainsKey("tool_choice").ShouldBeFalse();
    }

    [Fact]
    public async Task Boundary_missing_and_invalid_probabilities_keep_original_instance()
    {
        var client = new DecisionStub
        {
            Answer = _ => JsonNode.Parse("""{"answers":{"t0":{"probabilities":{"use":0.5}},"t1":{"probabilities":{"use":"0"}},"t2":{"probabilities":{"use":-0.1}},"t3":{"probabilities":{"use":1.1}}}}""")!.AsObject()
        };
        string body = Body(5).ToJsonString();
        ReferenceEquals(await Create(client).FilterAsync("/responses", body, Ct), body).ShouldBeTrue();
    }

    [Fact]
    public async Task Sequential_chunks_share_state_and_failed_second_chunk_stops_pass()
    {
        var client = new DecisionStub();
        string body = Body(45).ToJsonString();
        await Create(client).FilterAsync("/responses", body, Ct);
        client.Requests.Select(request => request["questions"]!.AsObject().Count).ToArray().ShouldBe([20, 20, 5]);
        client.Requests.Select(request => request["state"]!.GetValue<string>()).Distinct().Count().ShouldBe(1);

        client = new DecisionStub { FailCall = 2 };
        JsonObject result = JsonNode.Parse(await Create(client).FilterAsync("/responses", body, Ct))!.AsObject();
        client.Requests.Count.ShouldBe(2);
        result["tools"]!.AsArray().Count.ShouldBe(25);
        result["tools"]![0]!["name"]!.GetValue<string>().ShouldBe("tool20");
    }

    [Fact]
    public async Task State_is_latest_first_capped_and_instructions_are_bounded()
    {
        JsonObject body = Body(1);
        body["input"] = new JsonArray
        {
            (JsonNode)new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = new string('e', 4000) },
            (JsonNode)new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = "Latest" }
        };
        body["instructions"] = new string('s', 4000);
        body["tools"]![0]!["description"] = new string('d', 400);
        var client = new DecisionStub();
        await Create(client).FilterAsync("/responses", body.ToJsonString(), Ct);
        string state = client.Requests.Single()["state"]!.GetValue<string>();
        state.Length.ShouldBe(2000);
        state.ShouldStartWith("Latest\ne");
        state.ShouldNotContain("sss");
        client.Requests.Single()["questions"]!["t0"]!["instructions"]!.GetValue<string>().Length.ShouldBe(120);
    }

    [Fact]
    public async Task System_text_is_extractable_when_no_conversation_text_exists()
    {
        string body = """{"messages":[{"role":"system","content":"Task"}],"tools":[{"type":"function","function":{"name":"f"}}]}""";
        var client = new DecisionStub();
        await Create(client).FilterAsync("/chat/completions", body, Ct);
        client.Requests.Single()["state"]!.GetValue<string>().ShouldBe("Task");
    }

    [Fact]
    public async Task Failure_is_original_instance_and_logs_reason_type_without_payload()
    {
        var logs = new FilterLogger();
        var client = new DecisionStub { FailCall = 1 };
        JsonObject root = Body(1);
        root["input"] = "secret prompt";
        root["tools"]![0]!["description"] = "secret schema";
        string body = root.ToJsonString();
        ReferenceEquals(await Create(client, logs).FilterAsync("/responses", body, Ct), body).ShouldBeTrue();
        logs.Events.Single().Level.ShouldBe(LogLevel.Warning);
        logs.Events.Single().Text.ShouldContain(nameof(HttpRequestException));
        logs.Events.Single().Text.ShouldNotContain("secret");
    }

    [Fact]
    public async Task Invalid_envelope_is_fail_open_and_caller_cancellation_is_not()
    {
        var client = new DecisionStub { Answer = _ => new JsonObject() };
        string body = Body(1).ToJsonString();
        ReferenceEquals(await Create(client).FilterAsync("/responses", body, Ct), body).ShouldBeTrue();

        using var abort = new CancellationTokenSource();
        var logs = new FilterLogger();
        client = new DecisionStub { Answer = _ => { abort.Cancel(); throw new OperationCanceledException(abort.Token); } };
        await Should.ThrowAsync<OperationCanceledException>(() => Create(client, logs).FilterAsync("/responses", body, abort.Token));
        logs.Events.ShouldBeEmpty();
    }

    private static DecisionToolsFilter Create(DecisionStub client, FilterLogger? logger = null, bool enabled = true) =>
        new(Options.Create(new ImposterOptions { DecisionsTools = new DecisionsToolsOptions { Enabled = enabled } }), client, logger ?? new FilterLogger());

    private static JsonObject Body(int count)
    {
        var tools = new JsonArray();
        for (int index = 0; index < count; index++)
        {
            tools.Add((JsonNode)new JsonObject { ["type"] = "function", ["name"] = $"tool{index}" });
        }

        return new JsonObject { ["input"] = "Task", ["tools"] = tools };
    }

    private static JsonObject Answers(JsonObject request, Func<string, double> probability)
    {
        var answers = new JsonObject();
        foreach (string key in request["questions"]!.AsObject().Select(pair => pair.Key))
        {
            answers[key] = new JsonObject { ["probabilities"] = new JsonObject { ["use"] = probability(key) } };
        }

        return new JsonObject { ["answers"] = answers };
    }

    private sealed class DecisionStub : IDecisionModelClient
    {
        public List<JsonObject> Requests { get; } = [];
        public Func<JsonObject, JsonObject> Answer { get; init; } = request => Answers(request, _ => 0);
        public int FailCall { get; init; }

        public Task<JsonObject> DecideAsync(JsonObject request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Requests.Count == FailCall)
            {
                throw new HttpRequestException("secret error payload");
            }

            return Task.FromResult(Answer(request));
        }
    }

    private sealed class FilterLogger : ILogger<DecisionToolsFilter>
    {
        public List<(LogLevel Level, string Text)> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Events.Add((logLevel, formatter(state, exception)));
    }
}
