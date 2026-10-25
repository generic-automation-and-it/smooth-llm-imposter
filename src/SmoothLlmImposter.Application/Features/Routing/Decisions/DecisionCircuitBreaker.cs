using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;

namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

/// <summary>
/// Consumer-local circuit breaker for the tool-filter classifier (HLD 014 LADR-11, NFR-05). One failed call
/// opens it for <see cref="DecisionsToolsOptions.CircuitBreakSeconds"/>; while open a pass makes no classifier
/// request, so it never reaches the shared gate. After the cooldown exactly one caller is admitted as a trial.
/// The breaker is owned by this consumer alone: a second decision-service consumer registers its own.
/// </summary>
internal sealed class DecisionCircuitBreaker(
    IOptions<ImposterOptions> options,
    TimeProvider timeProvider,
    ILogger<DecisionCircuitBreaker> logger)
{
    private const string Consumer = "tool-filter";

    private long _openUntilUtcTicks;
    private int _trialAdmitted;

    internal enum Admission
    {
        Closed,
        Trial,
        Skip
    }

    internal Admission TryEnter()
    {
        long openUntil = Interlocked.Read(ref _openUntilUtcTicks);
        if (openUntil == 0)
        {
            return Admission.Closed;
        }

        if (timeProvider.GetUtcNow().UtcTicks < openUntil)
        {
            return Admission.Skip;
        }

        return Interlocked.CompareExchange(ref _trialAdmitted, 1, 0) == 0
            ? Admission.Trial
            : Admission.Skip;
    }

    internal void RecordSuccess(bool trial, string model, string endpoint)
    {
        if (!trial)
        {
            return;
        }

        Interlocked.Exchange(ref _openUntilUtcTicks, 0);
        Interlocked.Exchange(ref _trialAdmitted, 0);
        logger.LogInformation(
            "Decisions tool filter circuit closed for {Consumer} via model {Model} at {Endpoint}: the trial call succeeded",
            Consumer, model, endpoint);
    }

    internal void RecordFailure(Exception failure, bool trial, string model, string endpoint)
    {
        if (!IsServiceFailure(failure))
        {
            return;
        }

        TimeSpan cooldown = TimeSpan.FromSeconds(options.Value.DecisionsTools.CircuitBreakSeconds);
        DateTimeOffset retryAt = timeProvider.GetUtcNow().Add(cooldown);
        Interlocked.Exchange(ref _openUntilUtcTicks, retryAt.UtcTicks);
        if (trial)
        {
            Interlocked.Exchange(ref _trialAdmitted, 0);
        }
        logger.LogError(
            "Decisions tool filter circuit opened for {Consumer} via model {Model} at {Endpoint}: {Cause}; no classifier request until {RetryAt:O}",
            Consumer, model, endpoint, Cause(failure), retryAt);
    }

    /// <summary>Clears the trial admission when the trial ended without a success verdict (cancellation or a non-service failure).</summary>
    internal void AbandonTrial() => Interlocked.Exchange(ref _trialAdmitted, 0);

    internal static bool IsServiceFailure(Exception failure) => failure switch
    {
        DecisionGateTimeoutException => false,
        DecisionServiceTimeoutException => true,
        HttpRequestException http => IsFailingStatus(http.StatusCode),
        _ => false
    };

    private static bool IsFailingStatus(HttpStatusCode? status)
    {
        if (status is null)
        {
            return true;
        }

        int code = (int)status.Value;
        return code is not 408 and not 429 && code >= 400;
    }

    private static string Cause(Exception failure) =>
        failure is HttpRequestException { StatusCode: { } status }
            ? $"HTTP {(int)status}"
            : failure.GetType().Name;
}
