using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

internal sealed class OptimizationCoordinator(
    IEnumerable<IOptimizationStrategy> strategies,
    ILogger<OptimizationCoordinator> logger) : IOptimizationCoordinator
{
    public async Task<string> OptimizeAsync(OptimizationRequest request, CancellationToken cancellationToken)
    {
        IOptimizationStrategy[] enabled = strategies.Where(s => s.Enabled).ToArray();
        if (enabled.Length == 0)
        {
            return request.Body;
        }

        Task<OptimizationEdit>[] tasks = enabled
            .Select(strategy => RunOneAsync(strategy, request, cancellationToken))
            .ToArray();

        OptimizationEdit[] results;
        try
        {
            results = await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }

        return Merge(request.Body, results);
    }

    private async Task<OptimizationEdit> RunOneAsync(
        IOptimizationStrategy strategy, OptimizationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await strategy.OptimizeAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            logger.LogWarning(failure, "Optimization strategy {Strategy} failed; contributing no edit", strategy.Name);
            return OptimizationEdit.None;
        }
    }

    private static string Merge(string originalBody, IReadOnlyList<OptimizationEdit> edits)
    {
        List<OptimizationPatch> all = edits.SelectMany(e => e.Patches).ToList();
        if (all.Count == 0)
        {
            return originalBody;
        }

        // Overlap: the same pointer claimed by more than one patch.
        if (all.Select(p => p.Pointer).Distinct(StringComparer.Ordinal).Count() != all.Count)
        {
            return originalBody;
        }

        JsonObject root;
        try
        {
            root = JsonNodeMaterializer.ParseObject(originalBody);
        }
        catch
        {
            return originalBody;
        }

        foreach (OptimizationPatch patch in all)
        {
            if (!TrySet(root, patch.Pointer, patch.Replacement))
            {
                return originalBody;
            }
        }

        return root.ToJsonString();
    }

    private static bool TrySet(JsonObject root, string pointer, JsonNode? value)
    {
        if (string.IsNullOrEmpty(pointer) || !pointer.StartsWith('/'))
        {
            return false;
        }

        string[] segments = pointer.Substring(1).Split('/', StringSplitOptions.None)
            .Select(Unescape).ToArray();

        JsonNode? current = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            current = Navigate(current, segments[i]);
            if (current is null)
            {
                return false;
            }
        }

        string last = segments[^1];
        JsonNode? replacement = value?.DeepClone();

        if (current is JsonObject obj)
        {
            obj[last] = replacement;
            return true;
        }

        if (current is JsonArray arr &&
            int.TryParse(last, out int index) &&
            index >= 0 && index < arr.Count)
        {
            arr[index] = replacement;
            return true;
        }

        return false;
    }

    private static JsonNode? Navigate(JsonNode? node, string segment)
    {
        if (node is JsonObject obj)
        {
            return obj[segment];
        }

        if (node is JsonArray arr &&
            int.TryParse(segment, out int index) &&
            index >= 0 && index < arr.Count)
        {
            return arr[index];
        }

        return null;
    }

    private static string Unescape(string segment) =>
        segment.Replace("~1", "/").Replace("~0", "~");
}