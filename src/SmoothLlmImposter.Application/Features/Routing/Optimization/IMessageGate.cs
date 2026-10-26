namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// HLD 017 decision gate: answers whether one human message is worth rewriting. A failed or missing
/// answer continues to the optimizer (LADR-01, LADR-07).
/// </summary>
public interface IMessageGate
{
    /// <summary>Configured model name, for the circuit log only.</summary>
    string Model { get; }

    /// <summary>Configured endpoint without userinfo or query, for the circuit log only.</summary>
    string Endpoint { get; }

    Task<bool> CanOptimizeAsync(string humanText, CancellationToken cancellationToken);
}
