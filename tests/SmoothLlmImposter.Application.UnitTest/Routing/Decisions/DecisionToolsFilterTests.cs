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
              {"type":"function","function":{"name":"unused","description":"Tool description"}},{"type":"future","name":"opaque"},
              {"type":"function","function":{"name":"wanted"}}],
             "tool_choice":{"type":"allowed_tools","allowed_tools":{"mode":"auto","tools":[{"type":"function","function":{"name":"used"}},{"type":"function","function":{"name":"unused"}}]}},
             "functions":[{"name":"legacy"}],"function_call":{"name":"legacy"}}
            """;
        var client = new DecisionStub { Answer = request => Answers(request, key => key == "t4" ? 1 : 0) };
        JsonObject filtered = JsonNode.Parse(await Create(client).FilterAsync("/v1/chat/completions", body, Ct))!.AsObject();

        filtered["tools"]!.AsArray().Select(node => node?["type"]?.GetValue<string>()).ToArray()
            .ShouldBe(["function", "custom", "future", "function"]);
        filtered["messages"]!.ToJsonString().ShouldBe(JsonNode.Parse(body)!["messages"]!.ToJsonString());
        filtered["functions"]!.ToJsonString().ShouldBe("[{\"name\":\"legacy\"}]");
        filtered["function_call"]!["name"]!.GetValue<string>().ShouldBe("legacy");
        filtered["tool_choice"]!["allowed_tools"]!["tools"]!.AsArray().Count.ShouldBe(1);
        JsonObject request = client.Requests.Single();
        request["state"]!.GetValue<string>().ShouldBe("User request:\nLatest\n\nEarlier conversation (newest first):\nUser: Earlier");
        request["questions"]!.AsObject().Select(pair => pair.Key).ToArray().ShouldBe(["t2", "t4"]);
        request["questions"]!["t2"]!["instructions"]!.GetValue<string>()
            .ShouldBe("Will the assistant need the tool 'unused' to carry out the user's request? Tool: Tool description");
        request["questions"]!["t2"]!["criteria"]!.ToJsonString().ShouldBe(
            "{\"use\":\"the request needs what this tool does, or names this tool\",\"skip\":\"the request can be done fully without this tool\"}");
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
              {"type":"image_generation"},{"type":"local_shell"},{"type":"file_search"},
              {"type":"future","name":"opaque"}],
             "tool_choice":{"type":"allowed_tools","mode":"auto","tools":[{"type":"web_search"},{"type":"custom","name":"unused"}]}}
            """;
        var client = new DecisionStub { Answer = request => Answers(request, key => key == "t9" ? 1 : 0) };
        JsonObject result = JsonNode.Parse(await Create(client).FilterAsync("/responses", body, Ct))!.AsObject();
        result["tools"]!.AsArray().Count.ShouldBe(7);
        result["tool_choice"]!["tools"]!.AsArray().Count.ShouldBe(1);
        result["input"]!.ToJsonString().ShouldBe(JsonNode.Parse(body)!["input"]!.ToJsonString());
        JsonObject request = client.Requests.Single();
        request["state"]!.GetValue<string>()
            .ShouldBe("User request:\nLatest\n\nEarlier conversation (newest first):\nAssistant: Earlier answer\nUser: Earlier");
        request["questions"]!.AsObject().Select(pair => pair.Key).ToArray()
            .ShouldBe(["t5", "t6", "t7", "t8", "t9"]);
        request["questions"]!["t5"]!["instructions"]!.GetValue<string>()
            .ShouldBe("Will the assistant need the tool 'two' to carry out the user's request?");
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
              {"name":"remote","input_schema":{}},{"name":"unused","input_schema":{}},{"type":"future","name":"opaque","input_schema":{}},
              {"name":"wanted","input_schema":{}}],
             "tool_choice":{"type":"tool","name":"unused"},"mcp_servers":[{"name":"connector"}]}
            """;
        var client = new DecisionStub { Answer = request => Answers(request, key => key == "t5" ? 1 : 0) };
        JsonObject result = JsonNode.Parse(await Create(client).FilterAsync("/v1/messages", body, Ct))!.AsObject();
        result["tools"]!.AsArray().Count.ShouldBe(5);
        result.ContainsKey("tool_choice").ShouldBeFalse();
        result["messages"]!.ToJsonString().ShouldBe(JsonNode.Parse(body)!["messages"]!.ToJsonString());
        result["mcp_servers"]!.ToJsonString().ShouldBe("[{\"name\":\"connector\"}]");
        client.Requests.Single()["state"]!.GetValue<string>()
            .ShouldBe("User request:\nLatest\n\nEarlier conversation (newest first):\nUser: Earlier");
        client.Requests.Single()["questions"]!.AsObject().Select(pair => pair.Key).ToArray().ShouldBe(["t3", "t5"]);
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
    public async Task None_surviving_keeps_the_original_instance_and_logs_the_fallback()
    {
        JsonObject root = Body(2);
        root["tool_choice"] = "required";
        string body = root.ToJsonString();
        var logs = new FilterLogger();

        ReferenceEquals(await Create(new DecisionStub(), logs).FilterAsync("/responses", body, Ct), body).ShouldBeTrue();
        logs.Events.Where(entry => entry.Level > LogLevel.Debug).ShouldHaveSingleItem().Text.ShouldContain("kept all 2");
    }

    [Fact]
    public async Task All_below_threshold_with_protected_tools_keeps_them_and_tops_up_to_the_floor_by_score()
    {
        JsonObject root = Body(10);
        root["input"] = new JsonArray
        {
            (JsonNode)new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = "Task" },
            (JsonNode)new JsonObject { ["type"] = "function_call", ["name"] = "tool0", ["call_id"] = "a", ["arguments"] = "{}" },
            (JsonNode)new JsonObject { ["type"] = "function_call", ["name"] = "tool1", ["call_id"] = "b", ["arguments"] = "{}" },
            (JsonNode)new JsonObject { ["type"] = "function_call", ["name"] = "tool2", ["call_id"] = "c", ["arguments"] = "{}" }
        };
        var client = new DecisionStub
        {
            Answer = request => Answers(request, key => key switch { "t8" => 0.4, "t4" => 0.3, "t6" => 0.2, _ => 0.1 })
        };

        JsonObject result = JsonNode.Parse(await Create(client, minKeep: 5).FilterAsync("/responses", root.ToJsonString(), Ct))!.AsObject();

        client.Requests.Single()["questions"]!.AsObject().Count.ShouldBe(7);
        result["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToArray()
            .ShouldBe(["tool0", "tool1", "tool2", "tool4", "tool8"]);
    }

    [Fact]
    public async Task Always_kept_tools_are_never_asked_about_and_survive_pruning()
    {
        JsonObject root = Body(0);
        var tools = new JsonArray();
        foreach (string name in new[] { "Bash", "Read", "other0", "other1", "other2" })
        {
            tools.Add((JsonNode)new JsonObject { ["type"] = "function", ["name"] = name });
        }

        root["tools"] = tools;
        var client = new DecisionStub { Answer = request => Answers(request, key => key == "t2" ? 1 : 0) };

        JsonObject result = JsonNode.Parse(await Create(client, alwaysKeep: " Bash , Read ,").FilterAsync("/responses", root.ToJsonString(), Ct))!.AsObject();

        client.Requests.Single()["questions"]!.AsObject().Select(pair => pair.Key).ToArray().ShouldBe(["t2", "t3", "t4"]);
        result["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToArray()
            .ShouldBe(["Bash", "Read", "other0"]);
    }

    [Fact]
    public async Task Always_kept_names_match_case_sensitively_and_also_by_server_label_and_hosted_type()
    {
        string body = """
            {"input":"Task","tools":[{"type":"function","name":"bash"},{"type":"mcp","server_label":"core"},
             {"type":"web_search"},{"type":"function","name":"drop"}]}
            """;
        var client = new DecisionStub { Answer = request => Answers(request, key => key == "t0" ? 1 : 0) };

        JsonObject result = JsonNode.Parse(await Create(client, alwaysKeep: "Bash,core,web_search").FilterAsync("/responses", body, Ct))!.AsObject();

        client.Requests.Single()["questions"]!.AsObject().Select(pair => pair.Key).ToArray().ShouldBe(["t0", "t3"]);
        result["tools"]!.AsArray().Select(tool => tool!["type"]!.GetValue<string>()).ToArray()
            .ShouldBe(["function", "mcp", "web_search"]);
    }

    [Fact]
    public async Task Minimum_floor_restores_the_highest_scoring_removed_tools_in_catalog_order()
    {
        string body = Body(12).ToJsonString();
        var client = new DecisionStub
        {
            Answer = request => Answers(request, key => key switch
            {
                "t3" => 0.9,
                "t7" => 0.1,
                "t9" => 0.4,
                "t5" => 0.4,
                "t1" => 0.3,
                _ => 0
            })
        };

        JsonObject result = JsonNode.Parse(await Create(client, minKeep: 4).FilterAsync("/responses", body, Ct))!.AsObject();

        result["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToArray()
            .ShouldBe(["tool1", "tool3", "tool5", "tool9"]);
    }

    [Fact]
    public async Task Minimum_floor_counts_always_kept_tools_and_restores_only_what_is_needed()
    {
        JsonObject root = Body(10);
        root["tools"]![0]!["name"] = "Bash";
        string body = root.ToJsonString();
        var client = new DecisionStub { Answer = request => Answers(request, key => key == "t5" ? 1 : 0.1) };

        JsonObject result = JsonNode.Parse(await Create(client, minKeep: 3, alwaysKeep: "Bash").FilterAsync("/responses", body, Ct))!.AsObject();

        result["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToArray()
            .ShouldBe(["Bash", "tool1", "tool5"]);
    }

    [Fact]
    public async Task Catalogs_not_exceeding_the_minimum_make_no_classifier_call()
    {
        string body = Body(20).ToJsonString();
        var client = new DecisionStub();

        ReferenceEquals(await Create(client, minKeep: 20).FilterAsync("/responses", body, Ct), body).ShouldBeTrue();
        client.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Removed_tool_names_are_logged_with_control_characters_neutralised()
    {
        JsonObject root = Body(3);
        root["tools"]![1]!["name"] = "forged\n[ERR] line";
        var logs = new FilterLogger();
        var client = new DecisionStub { Answer = request => Answers(request, key => key == "t0" ? 1 : 0) };

        await Create(client, logs).FilterAsync("/responses", root.ToJsonString(), Ct);

        string summary = logs.Events.Single(entry => entry.Level == LogLevel.Information).Text;
        summary.ShouldContain("kept 1/3");
        summary.ShouldContain("removed forged?[ERR] line,tool2");
        summary.ShouldNotContain("\n");
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
        state.ShouldStartWith("User request:\nLatest\n\nEarlier conversation (newest first):\nUser: e");
        state.ShouldNotContain("sss");
        client.Requests.Single()["questions"]!["t0"]!["instructions"]!.GetValue<string>().Length.ShouldBe(300);
    }

    [Fact]
    public async Task Claude_code_turns_send_only_the_human_request_as_state()
    {
        string body = """
            {"system":"System","messages":[
              {"role":"user","content":[{"type":"text","text":"<system-reminder>\nQuotes <system-reminder> and </system-reminder> mid-block\n</system-reminder>"},{"type":"text","text":"Review the diff"}]},
              {"role":"system","content":"SessionStart hook output"},
              {"role":"assistant","content":[{"type":"text","text":"Fetching the diff"},{"type":"tool_use","name":"Bash"}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"x","content":"output"},{"type":"text","text":"PostToolUse hook output"}]},
              {"role":"system","content":[{"type":"text","text":"<total_tokens>14999994 tokens left</total_tokens>"}]}],
             "tools":[{"name":"Bash","input_schema":{}},{"name":"GetWorkspaceDiff","input_schema":{}}]}
            """;
        var client = new DecisionStub();
        await Create(client).FilterAsync("/v1/messages", body, Ct);
        client.Requests.Single()["state"]!.GetValue<string>().ShouldBe("User request:\nReview the diff");
    }

    [Theory]
    [InlineData("Before <system-reminder>one</system-reminder> middle <system-reminder>two</system-reminder> after", "Before  middle  after")]
    [InlineData("Explain <system-reminder> tags", "Explain <system-reminder> tags")]
    [InlineData("<system-reminder>one</system-reminder> Explain <system-reminder> tags", "Explain <system-reminder> tags")]
    [InlineData("<system-reminder>before</system-reminder> Review the diff <system-reminder>after</system-reminder>", "Review the diff")]
    public async Task Reminder_spans_are_removed_from_message_text(string text, string expected)
    {
        JsonObject body = Body(1);
        body["input"] = new JsonArray { (JsonNode)new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = text } };
        var client = new DecisionStub();
        await Create(client).FilterAsync("/responses", body.ToJsonString(), Ct);
        client.Requests.Single()["state"]!.GetValue<string>().ShouldBe($"User request:\n{expected}");
    }

    [Theory]
    [InlineData("""["<local-command-caveat>Caveat: the messages below were generated while running local commands.</local-command-caveat>","<command-name>/model</command-name>\n<command-message>model</command-message>\n<command-args>claude-opus-5-5</command-args>","<local-command-stdout>Set model to claude-opus-5-5</local-command-stdout>","continue"]""", "continue")]
    [InlineData("""["<system_instruction>\nYou are working inside Conductor.\n</system_instruction>\n\nhi"]""", "hi")]
    [InlineData("""["<command-name>/model</command-name>\n<command-args>claude-opus-5-5</command-args>\n<local-command-stdout>Set model</local-command-stdout>","continue"]""", "continue")]
    [InlineData("""["<local-command-caveat>Caveat.</local-command-caveat>","<command-name>/model</command-name>\n<command-message>model</command-message>\n<command-args>claude-opus-5-5</command-args>","<local-command-stdout>Set model</local-command-stdout>","<command-message>ai-review</command-message>\n<command-name>/ai-review</command-name>\n<command-args>142 1=fix</command-args>"]""",
        "<command-message>ai-review</command-message>\n<command-name>/ai-review</command-name>\n<command-args>142 1=fix</command-args>")]
    [InlineData("""["<command-message>ai-review</command-message>\n<command-name>/ai-review</command-name>\n<command-args>142 1=fix</command-args>"]""",
        "<command-message>ai-review</command-message>\n<command-name>/ai-review</command-name>\n<command-args>142 1=fix</command-args>")]
    public async Task Harness_wrappers_are_removed_but_skill_invocations_are_kept(string parts, string expected)
    {
        var content = new JsonArray();
        foreach (JsonNode? part in JsonNode.Parse(parts)!.AsArray())
        {
            content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = part!.GetValue<string>() });
        }

        var body = new JsonObject
        {
            ["messages"] = new JsonArray { (JsonNode)new JsonObject { ["role"] = "user", ["content"] = content } },
            ["tools"] = new JsonArray { (JsonNode)new JsonObject { ["name"] = "Bash", ["input_schema"] = new JsonObject() } }
        };
        var client = new DecisionStub();
        await Create(client).FilterAsync("/v1/messages", body.ToJsonString(), Ct);
        client.Requests.Single()["state"]!.GetValue<string>().ShouldBe($"User request:\n{expected}");
    }

    [Theory]
    [InlineData("/v1/messages", """[{"name":"Bash","input_schema":{}},{"name":"Placeholder","input_schema":{},"defer_loading":true}]""")]
    [InlineData("/v1/messages", """[{"name":"Bash","input_schema":{}},{"type":"tool_search_tool_regex_20251119","name":"tool_search"}]""")]
    [InlineData("/responses", """[{"type":"function","name":"f"},{"type":"tool_search"}]""")]
    [InlineData("/responses", """[{"type":"namespace","name":"ns","tools":[{"type":"function","name":"g","defer_loading":true}]}]""")]
    public async Task Deferred_tool_loading_skips_the_pass(string path, string tools)
    {
        string body = $$"""{"input":"Task","messages":[{"role":"user","content":"Task"}],"tools":{{tools}}}""";
        var client = new DecisionStub();
        ReferenceEquals(await Create(client).FilterAsync(path, body, Ct), body).ShouldBeTrue();
        client.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Identical_requests_are_answered_from_cache_and_failures_are_not_cached()
    {
        string body = Body(25).ToJsonString();
        var client = new DecisionStub { Answer = request => Answers(request, key => key == "t0" ? 1 : 0), FailCall = 1 };
        DecisionToolsFilter filter = Create(client);

        ReferenceEquals(await filter.FilterAsync("/responses", body, Ct), body).ShouldBeTrue();
        string first = await filter.FilterAsync("/responses", body, Ct);
        string second = await filter.FilterAsync("/responses", body, Ct);

        client.Requests.Count.ShouldBe(3);
        second.ShouldBe(first);
        JsonNode.Parse(second)!["tools"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Tool_call_continuation_reuses_cached_answers_for_the_same_request()
    {
        JsonObject root = Body(45);
        string initial = root.ToJsonString();
        root["input"] = new JsonArray
        {
            (JsonNode)new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = "Task" },
            (JsonNode)new JsonObject { ["type"] = "function_call", ["name"] = "tool0", ["call_id"] = "c1", ["arguments"] = "{}" },
            (JsonNode)new JsonObject { ["type"] = "function_call_output", ["call_id"] = "c1", ["output"] = "done" }
        };
        string continuation = root.ToJsonString();
        var client = new DecisionStub { Answer = request => Answers(request, key => key is "t0" or "t1" ? 1 : 0) };
        DecisionToolsFilter filter = Create(client);

        await filter.FilterAsync("/responses", initial, Ct);
        string result = await filter.FilterAsync("/responses", continuation, Ct);

        client.Requests.Count.ShouldBe(3);
        JsonNode.Parse(result)!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToArray()
            .ShouldBe(["tool0", "tool1"]);
    }

    [Fact]
    public async Task Chunk_with_an_invalid_answer_is_not_cached_and_is_asked_again()
    {
        string body = Body(2).ToJsonString();
        int calls = 0;
        var client = new DecisionStub
        {
            Answer = request => ++calls == 1
                ? JsonNode.Parse("""{"answers":{"t0":{"probabilities":{"use":1}},"t1":{"probabilities":{"use":"bad"}}}}""")!.AsObject()
                : Answers(request, key => key == "t0" ? 1 : 0)
        };
        DecisionToolsFilter filter = Create(client);

        string first = await filter.FilterAsync("/responses", body, Ct);
        string second = await filter.FilterAsync("/responses", body, Ct);

        ReferenceEquals(first, body).ShouldBeTrue();
        client.Requests.Count.ShouldBe(2);
        client.Requests[1]["questions"]!.AsObject().Select(pair => pair.Key).ToArray().ShouldBe(["t1"]);
        JsonNode.Parse(second)!["tools"]!.AsArray().ShouldHaveSingleItem()!["name"]!.GetValue<string>().ShouldBe("tool0");
    }

    [Fact]
    public async Task Pruned_output_keeps_every_other_top_level_value_byte_for_byte()
    {
        string messages = """[ {"role" : "user", "content":"Review <b>this</b> caf\u00e9 \/ diff"} ]""";
        string body = $$$"""{ "model":"m",  "messages" : {{{messages}}}, "tools":[{"type":"function","function":{"name":"keep"}},{"type":"function","function":{"name":"drop"}}], "tool_choice":{"type":"function","function":{"name":"drop"}}, "stream" : true }""";
        var client = new DecisionStub { Answer = request => Answers(request, key => key == "t0" ? 1 : 0) };

        string result = await Create(client).FilterAsync("/chat/completions", body, Ct);

        result.ShouldContain($"\"messages\" : {messages}");
        result.ShouldContain("\"model\":\"m\"");
        result.ShouldContain("\"stream\" : true");
        result.ShouldNotContain("tool_choice");
        JsonNode.Parse(result)!["tools"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task System_text_is_extractable_when_no_conversation_text_exists()
    {
        string body = """{"messages":[{"role":"system","content":"Task"}],"tools":[{"type":"function","function":{"name":"f"}}]}""";
        var client = new DecisionStub();
        await Create(client).FilterAsync("/chat/completions", body, Ct);
        client.Requests.Single()["state"]!.GetValue<string>().ShouldBe("Context:\nTask");
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
        logs.Events.Single().Text.ShouldContain("status 400, model openjev");
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

    private static DecisionToolsFilter Create(
        DecisionStub client, FilterLogger? logger = null, bool enabled = true, int minKeep = 0, string alwaysKeep = "") =>
        new(Options.Create(new ImposterOptions
        {
            DecisionsTools = new DecisionsToolsOptions { Enabled = enabled, MinKeepTools = minKeep, AlwaysKeep = alwaysKeep }
        }), client, logger ?? new FilterLogger());

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
                throw new HttpRequestException("secret error payload", null, System.Net.HttpStatusCode.BadRequest);
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
