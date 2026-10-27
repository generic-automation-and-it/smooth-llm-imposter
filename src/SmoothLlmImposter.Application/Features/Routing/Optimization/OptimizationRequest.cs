using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// The input handed to every optimization strategy for a single request. <paramref name="Provider"/> is the
/// resolved route; <c>null</c> means no route context, which leaves every strategy on.
/// </summary>
public sealed record OptimizationRequest(
    string Path,
    string Body,
    CallerHeaders CallerHeaders,
    string InboundBody,
    string? InboundPath = null,
    ProviderRoute? Provider = null);
