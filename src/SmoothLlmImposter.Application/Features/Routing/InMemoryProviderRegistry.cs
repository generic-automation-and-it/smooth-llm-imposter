namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Ordered, thread-safe runtime provider registry. Dictionary order is preserved because routing is
/// first-match-wins; reads receive clones so callers cannot mutate the registry outside admin slices.
/// </summary>
internal sealed class InMemoryProviderRegistry : IProviderRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProviderOptions> _providers = new(StringComparer.Ordinal);

    public bool IsSeeded { get; private set; }

    public IReadOnlyDictionary<string, ProviderOptions> Snapshot()
    {
        lock (_gate)
        {
            return ProviderOptionsCloner.CloneDictionary(_providers);
        }
    }

    public bool TryGet(string key, out ProviderOptions provider)
    {
        lock (_gate)
        {
            if (_providers.TryGetValue(key, out ProviderOptions? found))
            {
                provider = ProviderOptionsCloner.Clone(found);
                return true;
            }
        }

        provider = new ProviderOptions();
        return false;
    }

    public void Seed(IReadOnlyDictionary<string, ProviderOptions> providers)
    {
        lock (_gate)
        {
            if (IsSeeded)
            {
                return;
            }

            _providers.Clear();
            foreach ((string key, ProviderOptions provider) in providers)
            {
                _providers[key] = ProviderOptionsCloner.Clone(provider);
            }

            IsSeeded = true;
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

    public bool TrySetEnabled(string key, bool enabled, out ProviderOptions? provider)
    {
        lock (_gate)
        {
            if (!_providers.TryGetValue(key, out ProviderOptions? existing))
            {
                provider = null;
                return false;
            }

            ProviderOptions updated = ProviderOptionsCloner.Clone(existing);
            updated.Enabled = enabled;
            _providers[key] = updated;
            provider = ProviderOptionsCloner.Clone(updated);
            return true;
        }
    }
}
