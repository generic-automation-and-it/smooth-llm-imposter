namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// One optimization unit: given the request, return an edit to its own slice of the body,
/// or <see cref="OptimizationEdit.None"/> when it makes no change (including when disabled).
/// A strategy's own settings decide whether it is on.
/// </summary>
public interface IOptimizationStrategy
{
    /// <summary>Human-readable name used in logs.</summary>
    string Name { get; }

    /// <summary>Whether this strategy is currently enabled.</summary>
    bool Enabled { get; }

    /// <summary>Produce an edit for the request, or <see cref="OptimizationEdit.None"/>.</summary>
    Task<OptimizationEdit> OptimizeAsync(OptimizationRequest request, CancellationToken cancellationToken);
}