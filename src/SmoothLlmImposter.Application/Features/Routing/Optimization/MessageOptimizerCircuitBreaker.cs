using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Optimizer;

namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>The optimizer LLM's own circuit (HLD 017 LADR-07).</summary>
internal sealed class MessageOptimizerCircuitBreaker(
    IOptions<LlmMessagesOptions> options,
    TimeProvider timeProvider,
    ILogger<MessageOptimizerCircuitBreaker> logger)
{
    private readonly SectionCircuitBreaker _breaker = new("message-optimizer", timeProvider, logger);

    internal SectionCircuitBreaker.Admission TryEnter() => _breaker.TryEnter();

    internal void RecordSuccess(bool trial, string model, string endpoint) =>
        _breaker.RecordSuccess(trial, model, endpoint);

    internal void RecordFailure(Exception failure, bool trial, string model, string endpoint) =>
        _breaker.RecordFailure(
            failure, trial, options.Value.CircuitBreakSeconds, model, endpoint);

    internal void AbandonTrial() => _breaker.AbandonTrial();
}
