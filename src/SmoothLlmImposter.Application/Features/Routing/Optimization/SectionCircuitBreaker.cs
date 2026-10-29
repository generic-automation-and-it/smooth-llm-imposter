using System.Net;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Application.Common.Clients.LocalLlm;

namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// One consumer-local circuit for an HLD 017 section (HLD 014 LADR-11, HLD 017 LADR-07).
/// One failed service call opens it for the section's cooldown; while open no call is made. After the
/// cooldown exactly one caller is admitted as a trial. Caller cancellation and local gate waits never trip it.
/// </summary>
internal sealed class SectionCircuitBreaker(
    string section,
    TimeProvider timeProvider,
    ILogger logger)
{
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
            "{Section} circuit closed via model {Model} at {Endpoint}: the trial call succeeded",
            section, model, endpoint);
    }

    internal void RecordFailure(
        Exception failure, bool trial, int cooldownSeconds, string model, string endpoint)
    {
        if (!IsServiceFailure(failure))
        {
            return;
        }

        TimeSpan cooldown = TimeSpan.FromSeconds(Math.Max(1, cooldownSeconds));
        DateTimeOffset retryAt = timeProvider.GetUtcNow().Add(cooldown);
        Interlocked.Exchange(ref _openUntilUtcTicks, retryAt.UtcTicks);
        if (trial)
        {
            Interlocked.Exchange(ref _trialAdmitted, 0);
        }

        logger.LogError(
            "{Section} circuit opened via model {Model} at {Endpoint}: {Cause}; no call until {RetryAt:O}",
            section, model, endpoint, Cause(failure), retryAt);
    }

    internal void AbandonTrial() => Interlocked.Exchange(ref _trialAdmitted, 0);

    internal static bool IsServiceFailure(Exception failure) => failure switch
    {
        LocalLlmGateTimeoutException => false,
        DecisionServiceTimeoutException => true,
        // HttpClient.Timeout surfaces as TaskCanceledException with the caller's token
        // NOT cancelled; caller cancellation is filtered by the caller before
        // RecordFailure is invoked and must never be passed here.
        TaskCanceledException => true,
        HttpRequestException http => IsFailingStatus(http.StatusCode),
        _ => false
    };

    internal static string Endpoint(string baseUrl, string path)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? root))
        {
            return "unparsed";
        }

        string relative = string.IsNullOrWhiteSpace(path) ? "/" : path;
        int query = relative.IndexOf('?');
        if (query >= 0)
        {
            relative = relative[..query];
        }
        if (!relative.StartsWith('/'))
        {
            relative = "/" + relative;
        }

        string port = root.IsDefaultPort ? string.Empty : ":" + root.Port;
        return $"{root.Scheme}://{root.Host}{port}{root.AbsolutePath.TrimEnd('/')}{relative}";
    }

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
