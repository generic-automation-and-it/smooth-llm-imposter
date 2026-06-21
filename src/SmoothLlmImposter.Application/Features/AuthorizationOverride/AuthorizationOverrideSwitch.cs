using System.Collections.Concurrent;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.AuthorizationOverride;

/// <summary>
/// Thread-safe, in-memory implementation of <see cref="IAuthorizationOverrideSwitch"/>. State is a single
/// boolean per provider held in process memory only — no I/O, O(1) reads on the passthrough hot path
/// (NFR-003). Registered as a singleton; a fresh instance reads OFF for every dialect (fail-safe default).
/// </summary>
internal sealed class AuthorizationOverrideSwitch : IAuthorizationOverrideSwitch
{
    private readonly ConcurrentDictionary<AuthorizationOverrideKey, bool> _enabled = new();

    public bool IsEnabled(ApiDialect dialect, string providerName) =>
        _enabled.TryGetValue(Key(dialect, providerName), out bool enabled) && enabled;

    public void Enable(ApiDialect dialect, string providerName) => _enabled[Key(dialect, providerName)] = true;

    public void Disable(ApiDialect dialect, string providerName) => _enabled[Key(dialect, providerName)] = false;

    private static AuthorizationOverrideKey Key(ApiDialect dialect, string providerName) =>
        new(dialect, providerName.Trim().ToUpperInvariant());

    private readonly record struct AuthorizationOverrideKey(ApiDialect Dialect, string ProviderName);
}
