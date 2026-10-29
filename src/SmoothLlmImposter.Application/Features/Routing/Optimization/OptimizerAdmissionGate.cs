namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// Admission gates of the optimizer LLM: one per distinct endpoint and model, one call in flight each, across
/// every request in the process (HLD 017 LADR-03, #164 FR-16). Same shape as the decision service's keyed
/// gates (HLD 015 LADR-05), but a separate registry: the optimizer and the decision service never share a turn.
/// Keys come only from the configured optimizer transport, so the set stays bounded. The wait runs on the
/// injected clock and creates its timer only when the gate is taken.
/// </summary>
internal sealed class OptimizerAdmissionGate(TimeProvider timeProvider) : IDisposable
{
    private readonly Dictionary<(string Endpoint, string Model), SemaphoreSlim> _gates = [];
    private readonly Lock _lock = new();

    public async Task<Admission> EnterAsync(
        string endpoint, string model, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SemaphoreSlim semaphore = GateFor((endpoint, model));
        if (semaphore.Wait(0, CancellationToken.None))
        {
            return new Admission(semaphore);
        }

        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await semaphore.WaitAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OptimizerAdmissionTimeoutException(timeout);
        }

        return new Admission(semaphore);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (SemaphoreSlim semaphore in _gates.Values)
            {
                semaphore.Dispose();
            }

            _gates.Clear();
        }
    }

    private SemaphoreSlim GateFor((string Endpoint, string Model) key)
    {
        lock (_lock)
        {
            if (!_gates.TryGetValue(key, out SemaphoreSlim? semaphore))
            {
                semaphore = new SemaphoreSlim(1, 1);
                _gates[key] = semaphore;
            }

            return semaphore;
        }
    }

    internal readonly struct Admission(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
