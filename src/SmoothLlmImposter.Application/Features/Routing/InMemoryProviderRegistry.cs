using System.Diagnostics.CodeAnalysis;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Ordered, thread-safe runtime provider registry. Dictionary order is preserved because routing is
/// first-match-wins; reads receive clones so callers cannot mutate the registry outside admin slices.
/// </summary>
internal sealed class InMemoryProviderRegistry : IProviderRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProviderOptions> _providers = new(StringComparer.Ordinal);

    // volatile: read before acquiring the snapshot lock on request scopes, so the write in Seed() must
    // publish with release semantics or a weakly-ordered CPU could observe a stale false.
    private volatile bool _isSeeded;

    public bool IsSeeded => _isSeeded;

    public IReadOnlyDictionary<string, ProviderOptions> Snapshot()
    {
        lock (_gate)
        {
            return ProviderOptionsCloner.CloneDictionary(_providers);
        }
    }

    public bool TryGet(string key, [NotNullWhen(true)] out ProviderOptions? provider)
    {
        lock (_gate)
        {
            if (_providers.TryGetValue(key, out ProviderOptions? found))
            {
                provider = ProviderOptionsCloner.Clone(found);
                return true;
            }
        }

        provider = null;
        return false;
    }

    public void Seed(IReadOnlyDictionary<string, ProviderOptions> providers)
    {
        lock (_gate)
        {
            if (_isSeeded)
            {
                return;
            }

            _providers.Clear();
            foreach ((string key, ProviderOptions provider) in providers)
            {
                _providers[key] = ProviderOptionsCloner.Clone(provider);
            }

            _isSeeded = true;
        }
    }

    public void Upsert(string key, ProviderOptions provider)
    {
        lock (_gate)
        {
            _providers[key] = ProviderOptionsCloner.Clone(provider);
        }
    }

    public bool Delete(string key)
    {
        lock (_gate)
        {
            return _providers.Remove(key);
        }
    }
}
