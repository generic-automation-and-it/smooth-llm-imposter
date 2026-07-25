using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Detects the in-band routing probe (HLD 010) — a request whose last user message is exactly
/// <c>who?</c> — and returns a dialect-shaped synthetic reply describing the resolved route and
/// auth scheme. String-out so HTTP concerns stay in the Host.
/// </summary>
public interface IWhoMessageResponder
{
    /// <summary>
    /// Inspects <paramref name="requestBody"/> (the raw inbound body, NOT the transformed body)
    /// against the given <paramref name="plan"/>. When the probe triggers, writes the synthetic
    /// reply JSON to <paramref name="responseJson"/> and returns <c>true</c>; otherwise leaves
    /// <paramref name="responseJson"/> <c>null</c> and returns <c>false</c>, and the caller must
    /// forward the request as usual.
    /// </summary>
    /// <remarks>
    /// The trigger does NOT fire when the body's <c>stream</c> field is <c>true</c> (LADR-05) or
    /// when the last user message contains any non-text content part. A malformed or unexpected
    /// body shape returns <c>false</c> rather than throwing — the responder is a pure predicate
    /// that does not depend on the caller having validated JSON shape first.
    /// </remarks>
    bool TryBuildResponse(ApiDialect dialect, string requestBody, RoutePlan plan, out string? responseJson);
}
