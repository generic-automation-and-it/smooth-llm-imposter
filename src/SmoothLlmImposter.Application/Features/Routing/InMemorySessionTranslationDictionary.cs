using System.Collections.Concurrent;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Default <see cref="ISessionTranslationDictionary"/> backed by a process-lifetime
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>. No eviction, no clear, no remove (NFR-04).
/// Registered as DI singleton so all scopes share the same map.
/// </summary>
internal sealed class InMemorySessionTranslationDictionary : ISessionTranslationDictionary
{
    private readonly ConcurrentDictionary<string, string> _map = new(StringComparer.Ordinal);

    public bool TryTranslate(string? callerId, out string? syntheticId)
    {
        if (string.IsNullOrWhiteSpace(callerId))
        {
            syntheticId = null;
            return false;
        }

        if (_map.TryGetValue(callerId, out var value))
        {
            syntheticId = value;
            return true;
        }

        syntheticId = null;
        return false;
    }

    public bool TryAdd(string? callerId, out string? syntheticId)
    {
        if (string.IsNullOrWhiteSpace(callerId))
        {
            syntheticId = null;
            return false;
        }

        // First-write wins: only generate the synthetic id if no mapping exists yet.
        // We use ContainsKey + TryAdd instead of the eager value overload of GetOrAdd
        // so that a Guid is not minted on the existing-key path. (The factory overload
        // of GetOrAdd only invokes the factory on a miss, so this rationale is the
        // eager-overload case.)
        if (_map.ContainsKey(callerId))
        {
            syntheticId = _map[callerId];
            return false;
        }

        syntheticId = Guid.NewGuid().ToString("N");
        if (_map.TryAdd(callerId, syntheticId))
        {
            return true;
        }

        // Race: another thread inserted between ContainsKey and TryAdd. Use their value.
        syntheticId = _map[callerId];
        return false;
    }
}
