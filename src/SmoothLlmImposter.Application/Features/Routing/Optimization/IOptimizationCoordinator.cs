namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// Runs the enabled optimization strategies concurrently and merges their edits into the
/// original body once. Host code calls only the coordinator.
/// </summary>
public interface IOptimizationCoordinator
{
    /// <summary>
    /// Optimize the request body. Returns the merged body, or the original body when no edit
    /// applies or edits overlap.
    /// </summary>
    Task<string> OptimizeAsync(OptimizationRequest request, CancellationToken cancellationToken);
}