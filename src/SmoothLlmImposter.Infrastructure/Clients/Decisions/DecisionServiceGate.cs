using SmoothLlmImposter.Application.Common.Clients.Decisions;

namespace SmoothLlmImposter.Infrastructure.Clients.Decisions;

/// <summary>
/// Process-wide admission gate of the shared decision service (HLD 015 LADR-03, NFR-01). A singleton in its
/// own right rather than a field on a client, so every client instance and consumer shares one budget.
/// </summary>
internal sealed class DecisionServiceGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<Admission> EnterAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!await _semaphore.WaitAsync(timeout, cancellationToken))
        {
            throw new DecisionGateTimeoutException("The decision service concurrency gate timed out.");
        }

        return new Admission(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    internal readonly struct Admission(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
