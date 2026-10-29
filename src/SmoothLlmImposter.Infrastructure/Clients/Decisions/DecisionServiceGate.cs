using SmoothLlmImposter.Application.Common.Clients.Decisions;

namespace SmoothLlmImposter.Infrastructure.Clients.Decisions;

/// <summary>
/// Admission gates of the shared decision service: one per distinct endpoint and model (HLD 015 LADR-05,
/// NFR-01 per key). Sections resolved to the same pair take turns; different pairs run independently. Keys
/// come only from the profiles of registered decision sections, so the set stays bounded. A singleton in its
/// own right rather than a field on a client, so every client instance and consumer shares the same gates.
/// </summary>
internal sealed class DecisionServiceGate : IDisposable
{
    private readonly Dictionary<(string Endpoint, string Model), SemaphoreSlim> _gates = [];
    private readonly Lock _lock = new();

    public async Task<Admission> EnterAsync(
        DecisionServiceOptions profile, TimeSpan timeout, CancellationToken cancellationToken)
    {
        SemaphoreSlim semaphore = GateFor((SanitizedEndpoint(profile.BaseUrl, profile.Path), profile.Model));
        if (!await semaphore.WaitAsync(timeout, cancellationToken))
        {
            throw new DecisionGateTimeoutException("The decision service concurrency gate timed out.");
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

    private static string SanitizedEndpoint(string baseUrl, string path)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? root))
        {
            return "unparsed";
        }

        string relative = string.IsNullOrWhiteSpace(path) ? "/" : path;
        int query = relative.IndexOf('?');
        if (query >= 0)
        {
            relative = relative[..query];
        }
        if (!relative.StartsWith('/'))
        {
            relative = "/" + relative;
        }

        string port = root.IsDefaultPort ? string.Empty : ":" + root.Port;
        return $"{root.Scheme}://{root.Host}{port}{root.AbsolutePath.TrimEnd('/')}{relative}";
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
