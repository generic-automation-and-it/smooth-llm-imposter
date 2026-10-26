namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// The input handed to every optimization strategy for a single request.
/// </summary>
public sealed record OptimizationRequest(
    string Path,
    string Body,
    CallerHeaders CallerHeaders,
    string InboundBody,
    string? InboundPath = null);