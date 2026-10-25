using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// The result of an <see cref="IOptimizationStrategy"/>: zero or more patches to its owned slices.
/// <see cref="None"/> is the no-op sentinel (no change).
/// </summary>
public sealed record OptimizationEdit(IReadOnlyList<OptimizationPatch> Patches)
{
    public static readonly OptimizationEdit None = new(Array.Empty<OptimizationPatch>());

    public bool IsNoOp => Patches is null || Patches.Count == 0;
}