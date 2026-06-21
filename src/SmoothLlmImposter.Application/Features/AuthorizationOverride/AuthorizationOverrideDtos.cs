namespace SmoothLlmImposter.Application.Features.AuthorizationOverride;

/// <summary>The observable on/off state of a provider's passthrough authorization override.</summary>
public sealed record AuthorizationOverrideState(string Dialect, string ProviderName, bool Enabled);

/// <summary>
/// Outcome of an arm (<c>PUT</c>) request. Distinguishes a successful arm from a refusal because the
/// dialect has no active stored credential to present (LADR-005), so the endpoint can map the latter to 403.
/// </summary>
public sealed record SetAuthorizationOverrideResult(bool Armed, AuthorizationOverrideState State)
{
    public bool NoActiveCredential => !Armed;
}
