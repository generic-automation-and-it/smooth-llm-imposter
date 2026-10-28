namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// No optimizer admission within the wait bound. Contention between requests, not a fault of the optimizer, so
/// the optimizer circuit never opens for it (HLD 017 LADR-07, NFR-03); the message is forwarded as written.
/// </summary>
internal sealed class OptimizerAdmissionTimeoutException(TimeSpan timeout)
    : Exception($"The optimizer admission gate did not admit the call within {timeout.TotalSeconds:0.###} s.");
