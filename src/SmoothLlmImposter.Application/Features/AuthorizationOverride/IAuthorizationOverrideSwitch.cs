using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.AuthorizationOverride;

/// <summary>
/// Per-dialect, in-memory switch that forces the passthrough path to present the active stored
/// credential as <c>Authorization: Bearer</c> (dropping <c>x-api-key</c>). Default OFF, never persisted,
/// reset on process restart (HLD 003 LADR-001). Read only on the passthrough branch (LADR-003).
/// </summary>
public interface IAuthorizationOverrideSwitch
{
    bool IsEnabled(ApiDialect dialect);

    void Enable(ApiDialect dialect);

    void Disable(ApiDialect dialect);
}
