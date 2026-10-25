namespace SmoothLlmImposter.Application.Common.Clients.Decisions;

/// <summary>
/// The process-wide decision-service gate did not admit this call within its wait budget. This is contention
/// on the shared budget, not a fault of the decision service, so the tool filter must not open its circuit
/// breaker for it (HLD 014 LADR-11, HLD 015 LADR-03).
/// </summary>
public sealed class DecisionGateTimeoutException(string message, Exception? innerException = null)
    : Exception(message, innerException);
