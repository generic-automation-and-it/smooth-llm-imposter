using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.Features.Routing.Optimization;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Optimization;

public class OptimizationCoordinatorTests
{
    private sealed class FakeStrategy : IOptimizationStrategy
    {
        public string Name { get; }
        public bool Enabled { get; set; } = true;
        public Func<OptimizationRequest, CancellationToken, Task<OptimizationEdit>> Handler { get; set; } =
            (_, _) => Task.FromResult(OptimizationEdit.None);

        public FakeStrategy(string name) => Name = name;

        public Task<OptimizationEdit> OptimizeAsync(OptimizationRequest request, CancellationToken ct) =>
            Handler(request, ct);
    }

    private static OptimizationCoordinator Build(params IOptimizationStrategy[] strategies) =>
        new(strategies, NullLogger<OptimizationCoordinator>.Instance);

    private static OptimizationRequest Req(string body) =>
        new("/v1/chat/completions", body, CallerHeaders.None, body);

    [Fact]
    public async Task No_enabled_strategies_returns_original_body()
    {
        var s = new FakeStrategy("a") { Enabled = false };
        var coord = Build(s);
        const string body = "{\"model\":\"gpt-3.5\",\"tools\":[]}";
        string result = await coord.OptimizeAsync(Req(body), CancellationToken.None);
        result.ShouldBe(body);
    }

    [Fact]
    public async Task No_edit_merge_returns_original_body()
    {
        var s = new FakeStrategy("a");
        var coord = Build(s);
        const string body = "{\"model\":\"gpt-3.5\",\"tools\":[]}";
        string result = await coord.OptimizeAsync(Req(body), CancellationToken.None);
        result.ShouldBe(body);
    }

    [Fact]
    public async Task Merges_disjoint_edits()
    {
        var s1 = new FakeStrategy("a")
        {
            Handler = (_, _) => Task.FromResult(new OptimizationEdit(
                new[] { new OptimizationPatch("/tools", JsonNode.Parse("[{\"name\":\"x\"}]")) }))
        };
        var s2 = new FakeStrategy("b")
        {
            Handler = (_, _) => Task.FromResult(new OptimizationEdit(
                new[] { new OptimizationPatch("/model", JsonNode.Parse("\"gpt-4\"")) }))
        };
        var coord = Build(s1, s2);
        const string body = "{\"model\":\"gpt-3.5\",\"tools\":[]}";
        string result = await coord.OptimizeAsync(Req(body), CancellationToken.None);
        JsonNode root = JsonNode.Parse(result)!;
        root["model"]!.GetValue<string>().ShouldBe("gpt-4");
        root["tools"]![0]!["name"]!.GetValue<string>().ShouldBe("x");
    }

    [Fact]
    public async Task Overlapping_edits_forward_original_body()
    {
        var s1 = new FakeStrategy("a")
        {
            Handler = (_, _) => Task.FromResult(new OptimizationEdit(
                new[] { new OptimizationPatch("/tools", JsonNode.Parse("[]")) }))
        };
        var s2 = new FakeStrategy("b")
        {
            Handler = (_, _) => Task.FromResult(new OptimizationEdit(
                new[] { new OptimizationPatch("/tools", JsonNode.Parse("[]")) }))
        };
        var coord = Build(s1, s2);
        const string body = "{\"model\":\"gpt-3.5\",\"tools\":[{\"name\":\"x\"}]}";
        string result = await coord.OptimizeAsync(Req(body), CancellationToken.None);
        result.ShouldBe(body);
    }

    [Fact]
    public async Task Failing_branch_contributes_no_edit_others_apply()
    {
        var failing = new FakeStrategy("failing")
        {
            Handler = (_, _) => throw new InvalidOperationException("boom")
        };
        var ok = new FakeStrategy("ok")
        {
            Handler = (_, _) => Task.FromResult(new OptimizationEdit(
                new[] { new OptimizationPatch("/model", JsonNode.Parse("\"gpt-4\"")) }))
        };
        var coord = Build(failing, ok);
        const string body = "{\"model\":\"gpt-3.5\",\"tools\":[]}";
        string result = await coord.OptimizeAsync(Req(body), CancellationToken.None);
        JsonNode.Parse(result)!["model"]!.GetValue<string>().ShouldBe("gpt-4");
    }

    [Fact]
    public async Task Cancelled_caller_token_propagates()
    {
        var s = new FakeStrategy("a")
        {
            Handler = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return OptimizationEdit.None;
            }
        };
        var coord = Build(s);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(
            () => coord.OptimizeAsync(Req("{\"model\":\"gpt-3.5\"}"), cts.Token));
    }

    [Fact]
    public async Task Single_failing_branch_does_not_cancel_others()
    {
        var failing = new FakeStrategy("failing")
        {
            Handler = async (_, _) =>
            {
                await Task.Delay(50);
                throw new InvalidOperationException("boom");
            }
        };
        var ok = new FakeStrategy("ok")
        {
            Handler = async (_, _) =>
            {
                await Task.Delay(10);
                return new OptimizationEdit(new[] { new OptimizationPatch("/model", JsonNode.Parse("\"gpt-4\"")) });
            }
        };
        var coord = Build(failing, ok);
        string result = await coord.OptimizeAsync(Req("{\"model\":\"gpt-3.5\"}"), CancellationToken.None);
        JsonNode.Parse(result)!["model"]!.GetValue<string>().ShouldBe("gpt-4");
    }

    [Fact]
    public async Task Timed_out_branch_contributes_no_edit()
    {
        var timedOut = new FakeStrategy("timedout")
        {
            Handler = (_, _) => throw new OperationCanceledException("timeout")
        };
        var ok = new FakeStrategy("ok")
        {
            Handler = (_, _) => Task.FromResult(new OptimizationEdit(
                new[] { new OptimizationPatch("/model", JsonNode.Parse("\"gpt-4\"")) }))
        };
        var coord = Build(timedOut, ok);
        const string body = "{\"model\":\"gpt-3.5\"}";
        string result = await coord.OptimizeAsync(Req(body), CancellationToken.None);
        JsonNode.Parse(result)!["model"]!.GetValue<string>().ShouldBe("gpt-4");
    }

    [Fact]
    public async Task Nested_message_edit_preserves_every_other_byte_and_cache_control()
    {
        const string body = """
            { "model":"m",  "messages":[{"role":"user","content":[{"type":"text","text":"Please kindly review this.","cache_control":{"type":"ephemeral"}}]}], "stream" : true }
            """;
        var s = new FakeStrategy("a")
        {
            Handler = (_, _) => Task.FromResult(new OptimizationEdit(
                new[] { new OptimizationPatch("/messages/0/content/0/text", JsonValue.Create("Review this.")) }))
        };
        var coord = Build(s);

        string result = await coord.OptimizeAsync(Req(body), CancellationToken.None);

        result.ShouldContain("\"model\":\"m\",  ");
        result.ShouldContain("\"stream\" : true");
        result.ShouldContain("\"cache_control\":{\"type\":\"ephemeral\"}");
        JsonNode.Parse(result)!["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>().ShouldBe("Review this.");
    }

    [Fact]
    public async Task Nested_patch_preserves_non_ascii_bytes_before_the_patched_span()
    {
        const string body = """
            { "note":"café 日本語 😀", "messages":[{"role":"user","content":"Please kindly review this."}], "stream" : true }
            """;
        var s = new FakeStrategy("a")
        {
            Handler = (_, _) => Task.FromResult(new OptimizationEdit(
                new[] { new OptimizationPatch("/messages/0/content", JsonValue.Create("Review this.")) }))
        };
        var coord = Build(s);

        string result = await coord.OptimizeAsync(Req(body), CancellationToken.None);

        result.ShouldContain("\"note\":\"café 日本語 😀\"");
        JsonNode.Parse(result)!["messages"]![0]!["content"]!.GetValue<string>().ShouldBe("Review this.");
    }

    [Fact]
    public async Task A_top_level_removal_and_a_nested_edit_merge_without_touching_other_bytes()
    {
        const string body = """
            { "model":"m", "messages":[{"role":"user","content":"Please kindly review this."}], "tools":[ {"type":"function"} ], "tool_choice":{"type":"function"}, "stream" : true }
            """;
        var s = new FakeStrategy("a")
        {
            Handler = (_, _) => Task.FromResult(new OptimizationEdit(new[]
            {
                new OptimizationPatch("/messages/0/content", JsonValue.Create("Review this.")),
                new OptimizationPatch("/tool_choice", null)
            }))
        };
        var coord = Build(s);

        string result = await coord.OptimizeAsync(Req(body), CancellationToken.None);

        result.ShouldNotContain("tool_choice");
        result.ShouldContain("\"tools\":[ {\"type\":\"function\"} ]");
        result.ShouldContain("\"stream\" : true");
        JsonNode.Parse(result)!["messages"]![0]!["content"]!.GetValue<string>().ShouldBe("Review this.");
    }

    [Fact]
    public async Task Enabled_strategies_start_before_either_one_completes()
    {
        var bothStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        FakeStrategy Strategy(string name) => new(name)
        {
            Handler = async (_, _) =>
            {
                if (Interlocked.Increment(ref started) == 2)
                {
                    bothStarted.TrySetResult(true);
                }
                await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                return OptimizationEdit.None;
            }
        };

        var coord = Build(Strategy("a"), Strategy("b"));

        string result = await coord.OptimizeAsync(Req("{\"model\":\"m\"}"), CancellationToken.None);

        result.ShouldBe("{\"model\":\"m\"}");
    }

    [Fact]
    public async Task A_missing_nested_pointer_forwards_the_original_body()
    {
        const string body = "{\"model\":\"m\",\"messages\":[]}";
        var s = new FakeStrategy("a")
        {
            Handler = (_, _) => Task.FromResult(new OptimizationEdit(
                new[] { new OptimizationPatch("/messages/0/content", JsonValue.Create("x")) }))
        };
        var coord = Build(s);

        string result = await coord.OptimizeAsync(Req(body), CancellationToken.None);

        result.ShouldBe(body);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    public async Task Malformed_or_non_object_body_returns_the_original(string body)
    {
        var s = new FakeStrategy("a")
        {
            Handler = (_, _) => Task.FromResult(new OptimizationEdit(
                new[] { new OptimizationPatch("/messages/0/content", JsonValue.Create("x")) }))
        };
        var coord = Build(s);

        string result = await coord.OptimizeAsync(Req(body), CancellationToken.None);

        result.ShouldBe(body);
    }

    [Fact]
    public async Task An_invalid_pointer_returns_the_original()
    {
        const string body = "{\"model\":\"m\"}";
        var s = new FakeStrategy("a")
        {
            Handler = (_, _) => Task.FromResult(new OptimizationEdit(
                new[] { new OptimizationPatch("messages/0", JsonValue.Create("x")) }))
        };
        var coord = Build(s);

        string result = await coord.OptimizeAsync(Req(body), CancellationToken.None);

        result.ShouldBe(body);
    }

    [Fact]
    public async Task A_nested_removal_returns_the_original()
    {
        const string body = "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}";
        var s = new FakeStrategy("a")
        {
            Handler = (_, _) => Task.FromResult(new OptimizationEdit(
                new[] { new OptimizationPatch("/messages/0/content", null) }))
        };
        var coord = Build(s);

        string result = await coord.OptimizeAsync(Req(body), CancellationToken.None);

        result.ShouldBe(body);
    }

    [Fact]
    public async Task A_top_level_patch_for_a_missing_key_returns_the_original()
    {
        const string body = "{\"model\":\"m\"}";
        var s = new FakeStrategy("a")
        {
            Handler = (_, _) => Task.FromResult(new OptimizationEdit(
                new[] { new OptimizationPatch("/tools", JsonNode.Parse("[]")) }))
        };
        var coord = Build(s);

        string result = await coord.OptimizeAsync(Req(body), CancellationToken.None);

        result.ShouldBe(body);
    }

    [Fact]
    public async Task A_failing_strategy_is_logged_without_the_exception_message()
    {
        const string body = "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"secret user text\"}]}";
        var logs = new CollectingLogger();
        var s = new FakeStrategy("a")
        {
            Handler = (_, _) => Task.FromException<OptimizationEdit>(new JsonException("bad: " + body))
        };
        var coord = new OptimizationCoordinator(new[] { s }, logs);

        string result = await coord.OptimizeAsync(Req(body), CancellationToken.None);

        result.ShouldBe(body);
        logs.Events.ShouldNotBeEmpty();
        logs.Events.ShouldAllBe(entry => !entry.Text.Contains("secret user text", StringComparison.Ordinal));
        logs.Events.ShouldAllBe(entry => entry.Error == null);
    }

    private sealed class CollectingLogger : ILogger<OptimizationCoordinator>
    {
        public List<(LogLevel Level, string Text, Exception? Error)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Events.Add((logLevel, formatter(state, exception), exception));
    }
}