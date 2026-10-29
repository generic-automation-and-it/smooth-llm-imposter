using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Application.Common.Clients.LocalLlm;

namespace SmoothLlmImposter.Infrastructure.Clients.Decisions;

/// <summary>
/// Admission of the shared decision service: every section takes the one process-wide <see cref="LocalLlmSlot"/>,
/// which the message optimizer shares (HLD 015 LADR-05, NFR-01). A singleton in its own right rather than a field
/// on a client, so every client instance and consumer shares the same slot.
/// </summary>
internal sealed class DecisionServiceGate(LocalLlmSlot slot)
{
    public async Task<Admission> EnterAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!await slot.TryEnterAsync(timeout, cancellationToken))
        {
            throw new DecisionGateTimeoutException("The decision service concurrency gate timed out.");
        }

        return new Admission(slot);
    }

    internal readonly struct Admission(LocalLlmSlot slot) : IDisposable
    {
        public void Dispose() => slot.Release();
    }
}
