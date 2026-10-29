using SmoothLlmImposter.Application.Features.Routing.Optimization;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Optimization;

public sealed class OptimizerAdmissionGateTests
{
    private const string Endpoint = "http://optimizer.test/v1/chat/completions";
    private const string Model = "opt-model";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QueueBound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_second_call_on_the_same_endpoint_and_model_waits_until_the_first_is_released()
    {
        var clock = new AdmissionClock();
        using var gate = new OptimizerAdmissionGate(clock);
        OptimizerAdmissionGate.Admission first = await gate.EnterAsync(Endpoint, Model, Wait, Token);
        Task waiting = clock.TimerCreated;

        Task<OptimizerAdmissionGate.Admission> second = gate.EnterAsync(Endpoint, Model, Wait, Token);
        await waiting.WaitAsync(QueueBound, Token);

        second.IsCompleted.ShouldBeFalse("the second call must queue while the first holds the gate");
        first.Dispose();
        (await second).Dispose();
    }

    [Theory]
    [InlineData("http://other.test/v1/chat/completions", Model)]
    [InlineData(Endpoint, "other-model")]
    public async Task A_different_endpoint_or_model_is_admitted_while_the_gate_is_held(string endpoint, string model)
    {
        var clock = new AdmissionClock();
        using var gate = new OptimizerAdmissionGate(clock);
        using OptimizerAdmissionGate.Admission held = await gate.EnterAsync(Endpoint, Model, Wait, Token);

        Task<OptimizerAdmissionGate.Admission> other = gate.EnterAsync(endpoint, model, Wait, Token);

        other.IsCompletedSuccessfully.ShouldBeTrue();
        (await other).Dispose();
        clock.ActiveTimers.ShouldBe(0, "an uncontended call starts no deadline");
    }

    [Fact]
    public async Task A_wait_past_the_bound_throws_the_admission_timeout_and_takes_no_slot()
    {
        var clock = new AdmissionClock();
        using var gate = new OptimizerAdmissionGate(clock);
        OptimizerAdmissionGate.Admission held = await gate.EnterAsync(Endpoint, Model, Wait, Token);
        Task waiting = clock.TimerCreated;

        Task<OptimizerAdmissionGate.Admission> late = gate.EnterAsync(Endpoint, Model, Wait, Token);
        await waiting.WaitAsync(QueueBound, Token);
        clock.Advance(Wait - TimeSpan.FromSeconds(1));
        late.IsCompleted.ShouldBeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));

        await Should.ThrowAsync<OptimizerAdmissionTimeoutException>(() => late);
        held.Dispose();
        await AssertExactlyOneSlotAsync(gate, clock);
    }

    [Fact]
    public async Task Caller_cancellation_while_waiting_propagates_and_releases_nothing_it_did_not_take()
    {
        var clock = new AdmissionClock();
        using var gate = new OptimizerAdmissionGate(clock);
        OptimizerAdmissionGate.Admission held = await gate.EnterAsync(Endpoint, Model, Wait, Token);
        using var caller = new CancellationTokenSource();
        Task waiting = clock.TimerCreated;

        Task<OptimizerAdmissionGate.Admission> cancelled = gate.EnterAsync(Endpoint, Model, Wait, caller.Token);
        await waiting.WaitAsync(QueueBound, Token);
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => cancelled);
        Task<OptimizerAdmissionGate.Admission> stillQueued = gate.EnterAsync(Endpoint, Model, Wait, Token);
        stillQueued.IsCompleted.ShouldBeFalse("the cancelled waiter must not have released the held slot");
        held.Dispose();
        (await stillQueued).Dispose();
        await AssertExactlyOneSlotAsync(gate, clock);
    }

    [Fact]
    public async Task An_already_cancelled_caller_is_not_admitted_even_when_the_gate_is_free()
    {
        var clock = new AdmissionClock();
        using var gate = new OptimizerAdmissionGate(clock);
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => gate.EnterAsync(Endpoint, Model, Wait, caller.Token));

        await AssertExactlyOneSlotAsync(gate, clock);
    }

    private static async Task AssertExactlyOneSlotAsync(OptimizerAdmissionGate gate, AdmissionClock clock)
    {
        Task<OptimizerAdmissionGate.Admission> first = gate.EnterAsync(Endpoint, Model, Wait, Token);
        first.IsCompletedSuccessfully.ShouldBeTrue("the slot must be free");
        Task waiting = clock.TimerCreated;
        Task<OptimizerAdmissionGate.Admission> second = gate.EnterAsync(Endpoint, Model, Wait, Token);
        await waiting.WaitAsync(QueueBound, Token);
        second.IsCompleted.ShouldBeFalse("the gate must admit exactly one call at a time");
        (await first).Dispose();
        (await second).Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
