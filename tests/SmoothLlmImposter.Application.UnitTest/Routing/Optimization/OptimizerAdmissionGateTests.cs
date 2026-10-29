using SmoothLlmImposter.Application.Common.Clients.LocalLlm;
using SmoothLlmImposter.Application.Features.Routing.Optimization;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Optimization;

public sealed class OptimizerAdmissionGateTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QueueBound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_second_call_on_the_same_endpoint_and_model_waits_until_the_first_is_released()
    {
        var clock = new AdmissionClock();
        using var slot = new LocalLlmSlot();
        var gate = new OptimizerAdmissionGate(clock, slot);
        OptimizerAdmissionGate.Admission first = await gate.EnterAsync(Wait, Token);
        Task waiting = clock.TimerCreated;

        Task<OptimizerAdmissionGate.Admission> second = gate.EnterAsync(Wait, Token);
        await waiting.WaitAsync(QueueBound, Token);

        second.IsCompleted.ShouldBeFalse("the second call must queue while the first holds the gate");
        first.Dispose();
        (await second).Dispose();
    }

    [Fact]
    public async Task An_optimizer_call_waits_while_another_local_llm_call_holds_the_shared_slot()
    {
        var clock = new AdmissionClock();
        using var slot = new LocalLlmSlot();
        var gate = new OptimizerAdmissionGate(clock, slot);
        slot.TryEnter().ShouldBeTrue("a decision call holds the shared slot");
        Task waiting = clock.TimerCreated;

        Task<OptimizerAdmissionGate.Admission> optimizerCall = gate.EnterAsync(Wait, Token);
        await waiting.WaitAsync(QueueBound, Token);

        optimizerCall.IsCompleted.ShouldBeFalse("an optimizer call must not overlap a decision call");
        slot.Release();
        (await optimizerCall).Dispose();
        slot.TryEnter().ShouldBeTrue("the optimizer released the shared slot");
        slot.Release();
    }

    [Fact]
    public async Task A_held_optimizer_admission_blocks_other_local_llm_calls()
    {
        var clock = new AdmissionClock();
        using var slot = new LocalLlmSlot();
        var gate = new OptimizerAdmissionGate(clock, slot);

        using (await gate.EnterAsync(Wait, Token))
        {
            slot.TryEnter().ShouldBeFalse("a decision call must not overlap an optimizer call");
        }

        slot.TryEnter().ShouldBeTrue();
        slot.Release();
    }

    [Fact]
    public async Task A_wait_past_the_bound_throws_the_admission_timeout_and_takes_no_slot()
    {
        var clock = new AdmissionClock();
        using var slot = new LocalLlmSlot();
        var gate = new OptimizerAdmissionGate(clock, slot);
        OptimizerAdmissionGate.Admission held = await gate.EnterAsync(Wait, Token);
        Task waiting = clock.TimerCreated;

        Task<OptimizerAdmissionGate.Admission> late = gate.EnterAsync(Wait, Token);
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
        using var slot = new LocalLlmSlot();
        var gate = new OptimizerAdmissionGate(clock, slot);
        OptimizerAdmissionGate.Admission held = await gate.EnterAsync(Wait, Token);
        using var caller = new CancellationTokenSource();
        Task waiting = clock.TimerCreated;

        Task<OptimizerAdmissionGate.Admission> cancelled = gate.EnterAsync(Wait, caller.Token);
        await waiting.WaitAsync(QueueBound, Token);
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => cancelled);
        Task<OptimizerAdmissionGate.Admission> stillQueued = gate.EnterAsync(Wait, Token);
        stillQueued.IsCompleted.ShouldBeFalse("the cancelled waiter must not have released the held slot");
        held.Dispose();
        (await stillQueued).Dispose();
        await AssertExactlyOneSlotAsync(gate, clock);
    }

    [Fact]
    public async Task An_already_cancelled_caller_is_not_admitted_even_when_the_gate_is_free()
    {
        var clock = new AdmissionClock();
        using var slot = new LocalLlmSlot();
        var gate = new OptimizerAdmissionGate(clock, slot);
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => gate.EnterAsync(Wait, caller.Token));

        await AssertExactlyOneSlotAsync(gate, clock);
    }

    private static async Task AssertExactlyOneSlotAsync(OptimizerAdmissionGate gate, AdmissionClock clock)
    {
        Task<OptimizerAdmissionGate.Admission> first = gate.EnterAsync(Wait, Token);
        first.IsCompletedSuccessfully.ShouldBeTrue("the slot must be free");
        Task waiting = clock.TimerCreated;
        Task<OptimizerAdmissionGate.Admission> second = gate.EnterAsync(Wait, Token);
        await waiting.WaitAsync(QueueBound, Token);
        second.IsCompleted.ShouldBeFalse("the gate must admit exactly one call at a time");
        (await first).Dispose();
        (await second).Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
