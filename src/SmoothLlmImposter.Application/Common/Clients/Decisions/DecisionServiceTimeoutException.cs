namespace SmoothLlmImposter.Application.Common.Clients.Decisions;

/// <summary>
/// A decision-service call exhausted its per-attempt timeout budget. The transport translates the HTTP
/// library's timeout rejection into this contract type so the tool filter can classify it as a service fault
/// without depending on the resilience library (HLD 014 LADR-11).
/// </summary>
public sealed class DecisionServiceTimeoutException(string message, Exception? innerException = null)
    : Exception(message, innerException);
