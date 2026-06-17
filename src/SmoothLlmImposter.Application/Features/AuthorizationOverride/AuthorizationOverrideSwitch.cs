using System.Collections.Concurrent;
using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.AuthorizationOverride;

/// <summary>
/// Thread-safe, in-memory implementation of <see cref="IAuthorizationOverrideSwitch"/>. State is a single
/// boolean per dialect held in process memory only — no I/O, O(1) reads on the passthrough hot path
/// (NFR-003). Registered as a singleton; a fresh instance reads OFF for every dialect (fail-safe default).
/// </summary>
internal sealed class AuthorizationOverrideSwitch : IAuthorizationOverrideSwitch
{
    private readonly ConcurrentDictionary<ApiDialect, bool> _enabled = new();

    public bool IsEnabled(ApiDialect dialect) => _enabled.TryGetValue(dialect, out bool enabled) && enabled;

    public void Enable(ApiDialect dialect) => _enabled[dialect] = true;

    public void Disable(ApiDialect dialect) => _enabled[dialect] = false;
}
