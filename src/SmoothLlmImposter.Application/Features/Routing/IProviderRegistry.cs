using System.Diagnostics.CodeAnalysis;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Runtime, in-memory provider registry (HLD 008). Seeded once from the resolved config/env baseline at
/// startup; thereafter <c>/admin/providers</c> CRUD is authoritative until restart. All reads return deep
/// clones so callers cannot mutate registry state, and writes store deep clones for the same reason.
/// </summary>
public interface IProviderRegistry
{
    /// <summary>True once the startup seeder has populated the registry from the resolved options baseline.</summary>
    bool IsSeeded { get; }

    /// <summary>A deep-cloned snapshot of every provider, in declaration order.</summary>
    IReadOnlyDictionary<string, ProviderOptions> Snapshot();

    /// <summary>
    /// Gets a deep clone of the provider registered under <paramref name="key"/>. Returns <c>false</c> and a
    /// <c>null</c> <paramref name="provider"/> when the key is absent.
    /// </summary>
    bool TryGet(string key, [NotNullWhen(true)] out ProviderOptions? provider);

    /// <summary>Populates the registry from the baseline once; subsequent calls are ignored (idempotent).</summary>
    void Seed(IReadOnlyDictionary<string, ProviderOptions> providers);

    /// <summary>Inserts or replaces the provider registered under <paramref name="key"/>.</summary>
    void Upsert(string key, ProviderOptions provider);

    /// <summary>Removes the provider registered under <paramref name="key"/>; returns <c>false</c> when absent.</summary>
    bool Delete(string key);
}
