namespace SmoothLlmImposter.Application.Common.Clients.LocalLlm;

/// <summary>
/// The process-wide local-LLM gate did not admit this call within its wait budget. This is contention on the
/// shared slot, not a fault of the decision or optimizer service, so no circuit breaker opens for it
/// (HLD 014 LADR-11, HLD 015 LADR-05).
/// </summary>
public sealed class LocalLlmGateTimeoutException(string message, Exception? innerException = null)
    : Exception(message, innerException);
