using System.Text.Json.Nodes;
using SmoothLlmImposter.Application.Features.Routing.Normalization;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

/// <summary>
/// L0 tests for the v1 Codex → OpenAI-SDK tool normalization (HLD 004). Rules are derived from the
/// observed upstream contract: keep only valid <c>function</c> tools (and <c>plugin</c>), flatten
/// <c>namespace</c> wrappers, drop names failing <c>^[A-Za-z_][A-Za-z0-9_-]*$</c>, clean dependent
/// <c>tool_choice</c>; idempotent; works on both flat (Responses) and nested (Chat) tool shapes.
/// </summary>
public class CodexToOpenAiSdkNormalizerTests
{
    private static JsonObject Normalize(string body)
    {
        JsonObject root = JsonNode.Parse(body)!.AsObject();
        new CodexToOpenAiSdkNormalizer().Normalize(root);
        return root;
    }

    private static string[] FunctionNames(JsonObject root) =>
        root["tools"] is JsonArray tools
            ? [.. tools.Select(t => t!["function"]?["name"]?.GetValue<string>() ?? t!["name"]?.GetValue<string>() ?? "")]
            : [];

    [Fact]
    public void Keeps_valid_function_tools_including_leading_underscore()
    {
        JsonObject result = Normalize("""
        {"model":"m","tools":[
          {"type":"function","function":{"name":"search_issues","parameters":{"type":"object"}}},
          {"type":"function","function":{"name":"_create_pull_request","parameters":{"type":"object"}}},
          {"type":"function","function":{"name":"get-issue-2"}}
        ]}
        """);

        FunctionNames(result).ShouldBe(["search_issues", "_create_pull_request", "get-issue-2"]);
    }

    [Fact]
    public void Drops_unsupported_tool_types()
    {
        JsonObject result = Normalize("""
        {"model":"m","tools":[
          {"type":"function","function":{"name":"keep_me"}},
          {"type":"custom","name":"apply_patch"},
          {"type":"web_search","external_web_access":true},
          {"type":"image_generation","output_format":"png"},
          {"type":"tool_search","execution":"client"}
        ]}
        """);

        FunctionNames(result).ShouldBe(["keep_me"]);
    }

    [Fact]
    public void Drops_function_with_empty_dotted_or_leading_digit_name()
    {
        JsonObject result = Normalize("""
        {"model":"m","tools":[
          {"type":"function","function":{"name":""}},
          {"type":"function","function":{"name":"multi_tool_use.parallel"}},
          {"type":"function","function":{"name":"2fa_check"}},
          {"type":"function","function":{"name":"ok_name"}}
        ]}
        """);

        FunctionNames(result).ShouldBe(["ok_name"]);
    }

    [Fact]
    public void Flattens_namespace_into_its_nested_function_tools()
    {
        JsonObject result = Normalize("""
        {"model":"m","tools":[
          {"type":"namespace","name":"mcp__codex_apps__github","tools":[
            {"type":"function","name":"_search_issues","parameters":{"type":"object"}},
            {"type":"function","name":"_create_pull_request"},
            {"type":"function","name":"bad.name"}
          ]},
          {"type":"function","function":{"name":"exec_command"}}
        ]}
        """);

        // Wrapper gone; valid inner tools surface as top-level (flat) functions; the dotted one dropped.
        FunctionNames(result).ShouldBe(["_search_issues", "_create_pull_request", "exec_command"]);
        result["tools"]!.AsArray().Any(t => t!["type"]?.GetValue<string>() == "namespace").ShouldBeFalse();
    }

    [Fact]
    public void Keeps_plugin_tool_type()
    {
        JsonObject result = Normalize("""
        {"model":"m","tools":[{"type":"plugin","name":"example_plugin"},{"type":"function","function":{"name":"f"}}]}
        """);

        result["tools"]!.AsArray().Count.ShouldBe(2);
        result["tools"]!.AsArray()[0]!["type"]!.GetValue<string>().ShouldBe("plugin");
    }

    [Fact]
    public void Removes_tools_and_tool_choice_when_nothing_survives()
    {
        JsonObject result = Normalize("""
        {"model":"m","tools":[{"type":"web_search"}],"tool_choice":{"type":"function","function":{"name":"gone"}}}
        """);

        result.ContainsKey("tools").ShouldBeFalse();
        result.ContainsKey("tool_choice").ShouldBeFalse();
    }

    [Fact]
    public void Removes_tool_choice_referencing_a_dropped_tool()
    {
        JsonObject result = Normalize("""
        {"model":"m",
         "tools":[{"type":"function","function":{"name":"kept"}},{"type":"custom","name":"dropped"}],
         "tool_choice":{"type":"function","function":{"name":"dropped"}}}
        """);

        FunctionNames(result).ShouldBe(["kept"]);
        result.ContainsKey("tool_choice").ShouldBeFalse();
    }

    [Fact]
    public void Keeps_tool_choice_referencing_a_surviving_tool()
    {
        JsonObject result = Normalize("""
        {"model":"m",
         "tools":[{"type":"function","function":{"name":"kept"}}],
         "tool_choice":{"type":"function","function":{"name":"kept"}}}
        """);

        result["tool_choice"]!["function"]!["name"]!.GetValue<string>().ShouldBe("kept");
    }

    [Fact]
    public void Keeps_string_tool_choice_untouched()
    {
        JsonObject result = Normalize("""
        {"model":"m","tools":[{"type":"function","function":{"name":"kept"}}],"tool_choice":"auto"}
        """);

        result["tool_choice"]!.GetValue<string>().ShouldBe("auto");
    }

    [Fact]
    public void Handles_flat_responses_shape_function_tools()
    {
        JsonObject result = Normalize("""
        {"model":"m","tools":[
          {"type":"function","name":"flat_ok","parameters":{"type":"object"}},
          {"type":"function","name":"flat.bad"}
        ]}
        """);

        FunctionNames(result).ShouldBe(["flat_ok"]);
    }

    [Fact]
    public void Is_noop_when_no_tools_present()
    {
        JsonObject result = Normalize("""{"model":"m","messages":[{"role":"user","content":"hi"}]}""");

        result.ContainsKey("tools").ShouldBeFalse();
        result["messages"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public void Is_idempotent()
    {
        const string body = """
        {"model":"m","tools":[
          {"type":"namespace","name":"ns","tools":[{"type":"function","name":"_x"}]},
          {"type":"custom","name":"drop"},
          {"type":"function","function":{"name":"keep"}}
        ],"tool_choice":{"type":"function","function":{"name":"keep"}}}
        """;

        JsonObject once = Normalize(body);
        string onceJson = once.ToJsonString();

        new CodexToOpenAiSdkNormalizer().Normalize(once);

        once.ToJsonString().ShouldBe(onceJson);
        FunctionNames(once).ShouldBe(["_x", "keep"]);
    }
}
