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
        // ConcurrentDictionary.GetOrAdd would generate the value even when the key already
        // exists, so we check first to avoid minting unused ids.
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
