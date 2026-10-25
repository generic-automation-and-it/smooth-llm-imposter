using System.Text.Json.Nodes;
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
    public async Task Nested_pointer_applies()
    {
        var s = new FakeStrategy("a")
        {
            Handler = (_, _) => Task.FromResult(new OptimizationEdit(
                new[] { new OptimizationPatch("/tools/0/name", JsonNode.Parse("\"renamed\"")) }))
        };
        var coord = Build(s);
        const string body = "{\"model\":\"gpt-3.5\",\"tools\":[{\"name\":\"x\"}]}";
        string result = await coord.OptimizeAsync(Req(body), CancellationToken.None);
        JsonNode.Parse(result)!["tools"]![0]!["name"]!.GetValue<string>().ShouldBe("renamed");
    }
}