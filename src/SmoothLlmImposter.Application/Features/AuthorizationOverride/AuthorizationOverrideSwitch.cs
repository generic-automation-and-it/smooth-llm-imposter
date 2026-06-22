using System.Collections.Concurrent;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.AuthorizationOverride;

/// <summary>
/// Thread-safe, in-memory implementation of <see cref="IAuthorizationOverrideSwitch"/>. State is a single
/// boolean per <c>(dialect, provider)</c> held in process memory only — no I/O, O(1) reads on the passthrough
/// hot path (NFR-003). Registered as a singleton; a fresh instance reads OFF for every provider (fail-safe
/// default). Provider keys are trimmed and compared case-insensitively (<see cref="StringComparison.OrdinalIgnoreCase"/>),
/// matching the credential stores and <see cref="Routing.ProviderAddressResolver"/> so the canonical provider
/// key is treated identically across the switch, the stores, and the resolver.
/// </summary>
internal sealed class AuthorizationOverrideSwitch : IAuthorizationOverrideSwitch
{
    private readonly ConcurrentDictionary<AuthorizationOverrideKey, bool> _enabled = new(AuthorizationOverrideKey.Comparer);

    public bool IsEnabled(ApiDialect dialect, string providerName) =>
        _enabled.TryGetValue(Key(dialect, providerName), out bool enabled) && enabled;

    public void Enable(ApiDialect dialect, string providerName) => _enabled[Key(dialect, providerName)] = true;

    public void Disable(ApiDialect dialect, string providerName) => _enabled[Key(dialect, providerName)] = false;

    private static AuthorizationOverrideKey Key(ApiDialect dialect, string providerName) =>
        new(dialect, providerName.Trim());

    private readonly record struct AuthorizationOverrideKey(ApiDialect Dialect, string ProviderName)
    {
        public static IEqualityComparer<AuthorizationOverrideKey> Comparer { get; } = new KeyComparer();

        private sealed class KeyComparer : IEqualityComparer<AuthorizationOverrideKey>
        {
            public bool Equals(AuthorizationOverrideKey x, AuthorizationOverrideKey y) =>
                x.Dialect == y.Dialect &&
                string.Equals(x.ProviderName, y.ProviderName, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(AuthorizationOverrideKey obj) =>
                HashCode.Combine(obj.Dialect, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ProviderName));
        }
    }
}
