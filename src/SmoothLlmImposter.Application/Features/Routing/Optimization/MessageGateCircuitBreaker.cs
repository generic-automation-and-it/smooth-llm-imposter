using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>The message gate's own circuit (HLD 017 LADR-07).</summary>
internal sealed class MessageGateCircuitBreaker(
    IOptions<ImposterOptions> options,
    TimeProvider timeProvider,
    ILogger<MessageGateCircuitBreaker> logger)
{
    private readonly SectionCircuitBreaker _breaker = new("message-gate", timeProvider, logger);

    internal SectionCircuitBreaker.Admission TryEnter() => _breaker.TryEnter();

    internal void RecordSuccess(bool trial, string model, string endpoint) =>
        _breaker.RecordSuccess(trial, model, endpoint);

    internal void RecordFailure(Exception failure, bool trial, string model, string endpoint) =>
        _breaker.RecordFailure(
            failure, trial, options.Value.DecisionsMessages.CircuitBreakSeconds, model, endpoint);

    internal void AbandonTrial() => _breaker.AbandonTrial();
}
