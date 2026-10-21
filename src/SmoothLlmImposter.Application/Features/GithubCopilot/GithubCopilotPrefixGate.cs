namespace SmoothLlmImposter.Application.Features.GithubCopilot;

/// <summary>
/// Per-prefix coordination for anything that mutates a reserved provider pair or its durable connection:
/// login commit/publication, refresh, and orphan deletion. A single gate per prefix serializes those
/// operations so a delete cannot race a commit into a half-published pair, while unrelated prefixes stay
/// independent. The gate never encloses the global registry lock across awaited I/O.
/// </summary>
public interface IGithubCopilotPrefixGate
{
    Task<IDisposable> AcquireAsync(string prefix, CancellationToken cancellationToken);
}

internal sealed class GithubCopilotPrefixGate : IGithubCopilotPrefixGate
{
    private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public async Task<IDisposable> AcquireAsync(string prefix, CancellationToken cancellationToken)
    {
        SemaphoreSlim semaphore;
        lock (_lock)
        {
            if (!_gates.TryGetValue(prefix, out semaphore!))
            {
                semaphore = new SemaphoreSlim(1, 1);
                _gates[prefix] = semaphore;
            }
        }

        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            semaphore.Release();
        }
    }
}
