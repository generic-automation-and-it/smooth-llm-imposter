using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Optimizer;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.Features.Routing.Optimization;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Optimization;

public sealed class MessageOptimizationStrategyTests
{
    [Fact]
    public async Task Walks_human_messages_in_order_and_returns_text_patches()
    {
        const string body = """
            {"model":"x","messages":[
              {"role":"system","content":"sys"},
              {"role":"user","content":"Please kindly review `foo()`."},
              {"role":"assistant","content":"ok"},
              {"role":"tool","content":"tool output"},
              {"role":"user","content":[{"type":"text","text":"Please ask @reviewer."},{"type":"image_url","image_url":{"url":"x"}}]}
            ],"tools":[{"type":"function","function":{"name":"Bash"}}]}
            """;
        var gate = new StubGate((_, _) => Task.FromResult(true));
        var optimizer = new StubOptimizer((_, text, _) => Task.FromResult(
            text.Contains("foo", StringComparison.Ordinal) ? "Review `foo()`." : "Ask @reviewer."));
        MessageOptimizationStrategy strategy = Create(gate: gate, optimizer: optimizer);

        OptimizationEdit edit = await strategy.OptimizeAsync(
            Request("/v1/chat/completions", body), TestContext.Current.CancellationToken);

        edit.Patches.Count.ShouldBe(2);
        edit.Patches[0].Pointer.ShouldBe("/messages/1/content");
        edit.Patches[1].Pointer.ShouldBe("/messages/4/content/0/text");
        gate.Texts.ShouldBe(["Please kindly review `foo()`.", "Please ask @reviewer."]);
        optimizer.Texts.ShouldBe(["Please kindly review `foo()`.", "Please ask @reviewer."]);
    }

    [Fact]
    public async Task A_cache_hit_replays_without_gate_or_optimizer_calls()
    {
        const string body = """{"model":"x","messages":[{"role":"user","content":"Please kindly review `foo()`."}]}""";
        var cache = new FakeCache();
        cache.Seed("Please kindly review `foo()`.", "Review `foo()`.");
        var gate = new StubGate((_, _) => Task.FromResult(true));
        var optimizer = new StubOptimizer((_, _, _) => Task.FromResult("unused"));
        MessageOptimizationStrategy strategy = Create(gate: gate, optimizer: optimizer, cache: cache);

        OptimizationEdit edit = await strategy.OptimizeAsync(
            Request("/v1/chat/completions", body), TestContext.Current.CancellationToken);

        edit.Patches.Single().Replacement!.GetValue<string>().ShouldBe("Review `foo()`.");
        gate.Calls.ShouldBe(0);
        optimizer.Calls.ShouldBe(0);
        cache.Stored.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_below_minimum_message_skips_both_models_and_is_not_cached()
    {
        const string body = """{"model":"x","messages":[{"role":"user","content":"short"}]}""";
        var cache = new FakeCache();
        var gate = new StubGate((_, _) => Task.FromResult(true));
        var optimizer = new StubOptimizer((_, _, _) => Task.FromResult("tiny"));
        MessageOptimizationStrategy strategy = Create(gate: gate, optimizer: optimizer, cache: cache, minChars: 100);

        OptimizationEdit edit = await strategy.OptimizeAsync(
            Request("/v1/chat/completions", body), TestContext.Current.CancellationToken);

        edit.IsNoOp.ShouldBeTrue();
        gate.Calls.ShouldBe(0);
        optimizer.Calls.ShouldBe(0);
        cache.Stored.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_negative_gate_answer_records_the_original_and_skips_the_optimizer()
    {
        const string body = """{"model":"x","messages":[{"role":"user","content":"Please kindly review `foo()`."}]}""";
        var cache = new FakeCache();
        var gate = new StubGate((_, _) => Task.FromResult(false));
        var optimizer = new StubOptimizer((_, _, _) => Task.FromResult("unused"));
        MessageOptimizationStrategy strategy = Create(gate: gate, optimizer: optimizer, cache: cache);

        OptimizationEdit edit = await strategy.OptimizeAsync(
            Request("/v1/chat/completions", body), TestContext.Current.CancellationToken);

        edit.IsNoOp.ShouldBeTrue();
        optimizer.Calls.ShouldBe(0);
        cache.Stored.ShouldBe([("Please kindly review `foo()`.", "Please kindly review `foo()`.")]);
    }

    [Fact]
    public async Task A_failed_gate_continues_to_the_optimizer()
    {
        const string body = """{"model":"x","messages":[{"role":"user","content":"Please kindly review `foo()`."}]}""";
        var gate = new StubGate((_, _) => Task.FromException<bool>(new InvalidOperationException("gate down")));
        var optimizer = new StubOptimizer((_, _, _) => Task.FromResult("Review `foo()`."));
        MessageOptimizationStrategy strategy = Create(gate: gate, optimizer: optimizer);

        OptimizationEdit edit = await strategy.OptimizeAsync(
            Request("/v1/chat/completions", body), TestContext.Current.CancellationToken);

        edit.Patches.Single().Replacement!.GetValue<string>().ShouldBe("Review `foo()`.");
        optimizer.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_optimizer_records_the_original()
    {
        const string body = """{"model":"x","messages":[{"role":"user","content":"Please kindly review `foo()`."}]}""";
        var cache = new FakeCache();
        var gate = new StubGate((_, _) => Task.FromResult(true));
        var optimizer = new StubOptimizer((_, _, _) =>
            Task.FromException<string>(new HttpRequestException("optimizer down")));
        MessageOptimizationStrategy strategy = Create(gate: gate, optimizer: optimizer, cache: cache);

        OptimizationEdit edit = await strategy.OptimizeAsync(
            Request("/v1/chat/completions", body), TestContext.Current.CancellationToken);

        edit.IsNoOp.ShouldBeTrue();
        cache.Stored.ShouldBe([("Please kindly review `foo()`.", "Please kindly review `foo()`.")]);
    }

    [Fact]
    public async Task A_failed_rewrite_gets_one_targeted_retry()
    {
        const string body = """{"model":"x","messages":[{"role":"user","content":"Please keep `foo()` intact."}]}""";
        var gate = new StubGate((_, _) => Task.FromResult(true));
        var optimizer = new StubOptimizer((instructions, _, _) => Task.FromResult(
            instructions.Contains("failed these checks", StringComparison.Ordinal)
                ? "Keep `foo()`."
                : "Keep foo() intact."));
        MessageOptimizationStrategy strategy = Create(gate: gate, optimizer: optimizer);

        OptimizationEdit edit = await strategy.OptimizeAsync(
            Request("/v1/chat/completions", body), TestContext.Current.CancellationToken);

        edit.Patches.Single().Replacement!.GetValue<string>().ShouldBe("Keep `foo()`.");
        optimizer.Calls.ShouldBe(2);
        optimizer.Instructions[1].ShouldContain("failed these checks");
        optimizer.Instructions[1].ShouldContain("code:`foo()`");
    }

    [Fact]
    public async Task A_second_failed_rewrite_forwards_the_original()
    {
        const string body = """{"model":"x","messages":[{"role":"user","content":"Please keep `foo()` intact."}]}""";
        var cache = new FakeCache();
        var gate = new StubGate((_, _) => Task.FromResult(true));
        var optimizer = new StubOptimizer((_, _, _) => Task.FromResult("Keep foo() intact."));
        MessageOptimizationStrategy strategy = Create(gate: gate, optimizer: optimizer, cache: cache);

        OptimizationEdit edit = await strategy.OptimizeAsync(
            Request("/v1/chat/completions", body), TestContext.Current.CancellationToken);

        edit.IsNoOp.ShouldBeTrue();
        optimizer.Calls.ShouldBe(2);
        cache.Stored.ShouldBe([("Please keep `foo()` intact.", "Please keep `foo()` intact.")]);
    }

    [Fact]
    public async Task A_switch_message_is_skipped()
    {
        const string body = """{"model":"x","messages":[{"role":"user","content":"--who?"}]}""";
        var gate = new StubGate((_, _) => Task.FromResult(true));
        var optimizer = new StubOptimizer((_, _, _) => Task.FromResult("probe"));
        MessageOptimizationStrategy strategy = Create(gate: gate, optimizer: optimizer);

        OptimizationEdit edit = await strategy.OptimizeAsync(
            Request("/v1/chat/completions", body), TestContext.Current.CancellationToken);

        edit.IsNoOp.ShouldBeTrue();
        gate.Calls.ShouldBe(0);
        optimizer.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task An_anthropic_tool_result_turn_is_skipped()
    {
        const string body = """
            {"model":"x","messages":[{"role":"user","content":[
              {"type":"tool_result","tool_use_id":"x","content":"output"},
              {"type":"text","text":"hook output"}]}]}
            """;
        var gate = new StubGate((_, _) => Task.FromResult(true));
        var optimizer = new StubOptimizer((_, _, _) => Task.FromResult("unused"));
        MessageOptimizationStrategy strategy = Create(gate: gate, optimizer: optimizer);

        OptimizationEdit edit = await strategy.OptimizeAsync(
            Request("/v1/messages", body), TestContext.Current.CancellationToken);

        edit.IsNoOp.ShouldBeTrue();
        gate.Calls.ShouldBe(0);
        optimizer.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Anthropic_text_is_rewritten_and_an_image_block_is_untouched()
    {
        const string body = """
            {"model":"x","messages":[{"role":"user","content":[
              {"type":"text","text":"Please kindly review `foo()`."},
              {"type":"image","source":{"type":"base64","data":"x"}}]}]}
            """;
        var gate = new StubGate((_, _) => Task.FromResult(true));
        var optimizer = new StubOptimizer((_, _, _) => Task.FromResult("Review `foo()`."));
        MessageOptimizationStrategy strategy = Create(gate: gate, optimizer: optimizer);

        OptimizationEdit edit = await strategy.OptimizeAsync(
            Request("/v1/messages", body), TestContext.Current.CancellationToken);

        edit.Patches.Single().Pointer.ShouldBe("/messages/0/content/0/text");
        edit.Patches.Single().Replacement!.GetValue<string>().ShouldBe("Review `foo()`.");
    }

    [Fact]
    public async Task Harness_spans_are_preserved_around_rewritten_segments()
    {
        const string body = """{"model":"x","messages":[{"role":"user","content":"Please kindly review <system-reminder>hidden</system-reminder> the code."}]}""";
        var gate = new StubGate((_, _) => Task.FromResult(true));
        var optimizer = new StubOptimizer((_, text, _) => Task.FromResult(
            text.Trim() == "Please kindly review" ? "Review " : " code."));
        MessageOptimizationStrategy strategy = Create(gate: gate, optimizer: optimizer);

        OptimizationEdit edit = await strategy.OptimizeAsync(
            Request("/v1/chat/completions", body), TestContext.Current.CancellationToken);

        edit.Patches.Single().Replacement!.GetValue<string>()
            .ShouldBe("Review <system-reminder>hidden</system-reminder> code.");
        optimizer.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_responses_request_is_untouched()
    {
        const string body = """{"model":"x","input":"Please kindly review this."}""";
        var gate = new StubGate((_, _) => Task.FromResult(true));
        var optimizer = new StubOptimizer((_, _, _) => Task.FromResult("Review this."));
        MessageOptimizationStrategy strategy = Create(gate: gate, optimizer: optimizer);

        OptimizationEdit edit = await strategy.OptimizeAsync(
            Request("/v1/responses", body), TestContext.Current.CancellationToken);

        edit.IsNoOp.ShouldBeTrue();
        gate.Calls.ShouldBe(0);
        optimizer.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_captured_session_replays_the_cached_rewrite_on_the_next_request()
    {
        const string body = """{"model":"x","messages":[{"role":"user","content":"Please kindly review `foo()`."}]}""";
        var gate = new StubGate((_, _) => Task.FromResult(true));
        var optimizer = new StubOptimizer((_, _, _) => Task.FromResult("Review `foo()`."));
        var cache = new SmoothLlmImposter.Application.Features.Routing.Optimization.MessageChecksumCache(
            TimeProvider.System, Options.Create(new LlmMessagesOptions { CacheTtlHours = 120 }));
        MessageOptimizationStrategy strategy = Create(gate: gate, optimizer: optimizer, cache: cache);
        CallerHeaders headers = new([new KeyValuePair<string, IReadOnlyList<string>>("session_id", ["s1"])]);

        await strategy.OptimizeAsync(Request("/v1/chat/completions", body, headers), TestContext.Current.CancellationToken);
        OptimizationEdit second = await strategy.OptimizeAsync(
            Request("/v1/chat/completions", body, headers), TestContext.Current.CancellationToken);

        optimizer.Calls.ShouldBe(1);
        gate.Calls.ShouldBe(1);
        second.Patches.Single().Replacement!.GetValue<string>().ShouldBe("Review `foo()`.");
    }

    [Fact]
    public async Task A_sessionless_request_is_re_optimized_every_time()
    {
        const string body = """{"model":"x","messages":[{"role":"user","content":"Please kindly review `foo()`."}]}""";
        var gate = new StubGate((_, _) => Task.FromResult(true));
        var optimizer = new StubOptimizer((_, _, _) => Task.FromResult("Review `foo()`."));
        var cache = new SmoothLlmImposter.Application.Features.Routing.Optimization.MessageChecksumCache(
            TimeProvider.System, Options.Create(new LlmMessagesOptions { CacheTtlHours = 120 }));
        MessageOptimizationStrategy strategy = Create(gate: gate, optimizer: optimizer, cache: cache);

        await strategy.OptimizeAsync(Request("/v1/chat/completions", body), TestContext.Current.CancellationToken);
        await strategy.OptimizeAsync(Request("/v1/chat/completions", body), TestContext.Current.CancellationToken);

        optimizer.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_mentioned_tool_name_is_protected_by_the_validator()
    {
        const string body = """
            {"model":"x","messages":[{"role":"user","content":"Please use Bash."}],
             "tools":[{"type":"function","function":{"name":"Bash"}}]}
            """;
        var gate = new StubGate((_, _) => Task.FromResult(true));
        var optimizer = new StubOptimizer((instructions, _, _) => Task.FromResult(
            instructions.Contains("failed these checks", StringComparison.Ordinal)
                ? "Use Bash."
                : "Use it."));
        MessageOptimizationStrategy strategy = Create(gate: gate, optimizer: optimizer);

        OptimizationEdit edit = await strategy.OptimizeAsync(
            Request("/v1/chat/completions", body), TestContext.Current.CancellationToken);

        edit.Patches.Single().Replacement!.GetValue<string>().ShouldBe("Use Bash.");
        optimizer.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_stores_nothing()
    {
        const string body = """{"model":"x","messages":[{"role":"user","content":"Please kindly review `foo()`."}]}""";
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cache = new FakeCache();
        var gate = new StubGate((_, token) => Task.FromCanceled<bool>(token));
        var optimizer = new StubOptimizer((_, _, _) => Task.FromResult("unused"));
        MessageOptimizationStrategy strategy = Create(gate: gate, optimizer: optimizer, cache: cache);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            strategy.OptimizeAsync(Request("/v1/chat/completions", body), cts.Token));

        cache.Stored.ShouldBeEmpty();
    }

    private static MessageOptimizationStrategy Create(
        StubGate gate,
        StubOptimizer optimizer,
        IMessageChecksumCache? cache = null,
        int minChars = 0,
        bool gateEnabled = true)
    {
        var options = new ImposterOptions
        {
            LlmMessages = new LlmMessagesOptions { Enabled = true, Model = "opt-model", MinChars = minChars },
            DecisionsMessages = new DecisionsMessagesOptions { Enabled = gateEnabled, MinProbability = 0.5 }
        };
        return new MessageOptimizationStrategy(
            Options.Create(options),
            gate,
            optimizer,
            cache ?? new FakeCache(),
            NullLogger<MessageOptimizationStrategy>.Instance);
    }

    private static OptimizationRequest Request(
        string path,
        string body,
        CallerHeaders? headers = null) =>
        new(path, body, headers ?? CallerHeaders.None, body);

    private sealed class StubGate(Func<string, CancellationToken, Task<bool>> decide) : IMessageGate
    {
        public int Calls { get; private set; }
        public List<string> Texts { get; } = [];

        public Task<bool> CanOptimizeAsync(string humanText, CancellationToken cancellationToken)
        {
            Calls++;
            Texts.Add(humanText);
            return decide(humanText, cancellationToken);
        }
    }

    private sealed class StubOptimizer(Func<string, string, CancellationToken, Task<string>> optimize) : IOptimizerClient
    {
        public int Calls { get; private set; }
        public List<string> Texts { get; } = [];
        public List<string> Instructions { get; } = [];

        public Task<string> OptimizeAsync(
            string systemInstructions,
            string userText,
            CancellationToken cancellationToken)
        {
            Calls++;
            Instructions.Add(systemInstructions);
            Texts.Add(userText);
            return optimize(systemInstructions, userText, cancellationToken);
        }
    }

    private sealed class FakeCache : IMessageChecksumCache
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public List<(string Original, string Forwarded)> Stored { get; } = [];

        public void Seed(string original, string forwarded) => _values[original] = forwarded;

        public bool TryGet(
            SessionIdentity session,
            CallerHeaders headers,
            string originalText,
            string optimizerVersion,
            out string forwardedText) =>
            _values.TryGetValue(originalText, out forwardedText!);

        public void Store(
            SessionIdentity session,
            CallerHeaders headers,
            string originalText,
            string optimizerVersion,
            string forwardedText)
        {
            Stored.Add((originalText, forwardedText));
            _values[originalText] = forwardedText;
        }
    }
}
