using SmoothLlmImposter.Application.Common.Clients.LocalLlm;

namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// Admission of the optimizer LLM: every optimizer call takes the one process-wide <see cref="LocalLlmSlot"/> that
/// the decision service also takes, so no optimizer call overlaps another optimizer or decision call (HLD 017
/// LADR-03, HLD 015 LADR-05, #164 FR-16). The wait runs on the injected clock and creates its timer only when the
/// slot is taken.
/// </summary>
internal sealed class OptimizerAdmissionGate(TimeProvider timeProvider, LocalLlmSlot slot)
{
    public async Task<Admission> EnterAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (slot.TryEnter())
        {
            return new Admission(slot);
        }

        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await slot.EnterAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OptimizerAdmissionTimeoutException(timeout);
        }

        return new Admission(slot);
    }

    internal readonly struct Admission(LocalLlmSlot slot) : IDisposable
    {
        public void Dispose() => slot.Release();
    }
}
