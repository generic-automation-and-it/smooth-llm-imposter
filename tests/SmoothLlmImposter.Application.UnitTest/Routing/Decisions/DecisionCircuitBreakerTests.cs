using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Application.Common.Clients.LocalLlm;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.Features.Routing.Decisions;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Decisions;

public sealed class DecisionCircuitBreakerTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.RequestTimeout, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public void Http_status_classification(HttpStatusCode status, bool expected)
    {
        var failure = new HttpRequestException("body", null, status);
        DecisionCircuitBreaker.IsServiceFailure(failure).ShouldBe(expected);
    }

    [Fact]
    public void Transport_and_timeout_failures_count_gate_contention_and_cancellation_do_not()
    {
        DecisionCircuitBreaker.IsServiceFailure(new HttpRequestException("transport")).ShouldBeTrue();
        DecisionCircuitBreaker.IsServiceFailure(new DecisionServiceTimeoutException("timeout")).ShouldBeTrue();
        DecisionCircuitBreaker.IsServiceFailure(new LocalLlmGateTimeoutException("gate")).ShouldBeFalse();
        DecisionCircuitBreaker.IsServiceFailure(new OperationCanceledException()).ShouldBeFalse();
        DecisionCircuitBreaker.IsServiceFailure(new JsonException("envelope")).ShouldBeFalse();
    }

    [Fact]
    public void One_failure_opens_and_skips_until_the_cooldown_then_admits_one_trial()
    {
        var clock = new ManualClock();
        var logs = new BreakerLogger();
        DecisionCircuitBreaker breaker = Create(clock, logs, cooldownSeconds: 30);
        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Closed);

        breaker.RecordFailure(new HttpRequestException("down", null, HttpStatusCode.InternalServerError), false, "model", "endpoint");

        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Skip);
        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Skip);
        logs.Events.ShouldContain(entry => entry.Level == LogLevel.Error && entry.Text.Contains("circuit opened"));
        logs.Events.ShouldContain(entry => entry.Text.Contains("HTTP 500"));
        logs.Events.ShouldNotContain(entry => entry.Text.Contains("down"));

        clock.Advance(TimeSpan.FromSeconds(29));
        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Skip);

        clock.Advance(TimeSpan.FromSeconds(1));
        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Trial);
        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Skip);

        breaker.RecordSuccess(trial: true, "model", "endpoint");
        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Closed);
        logs.Events.ShouldContain(entry => entry.Level == LogLevel.Information && entry.Text.Contains("circuit closed"));
    }

    [Fact]
    public void A_failed_trial_reopens_the_circuit_for_a_full_cooldown()
    {
        var clock = new ManualClock();
        DecisionCircuitBreaker breaker = Create(clock, new BreakerLogger(), cooldownSeconds: 30);
        breaker.RecordFailure(new HttpRequestException("down"), false, "model", "endpoint");

        clock.Advance(TimeSpan.FromSeconds(30));
        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Trial);
        breaker.RecordFailure(new HttpRequestException("down"), true, "model", "endpoint");

        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Skip);
        clock.Advance(TimeSpan.FromSeconds(29));
        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Skip);
        clock.Advance(TimeSpan.FromSeconds(1));
        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Trial);
    }

    [Fact]
    public void An_abandoned_trial_does_not_wedge_the_circuit()
    {
        var clock = new ManualClock();
        DecisionCircuitBreaker breaker = Create(clock, new BreakerLogger(), cooldownSeconds: 30);
        breaker.RecordFailure(new HttpRequestException("down"), false, "model", "endpoint");

        clock.Advance(TimeSpan.FromSeconds(30));
        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Trial);
        breaker.AbandonTrial();

        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Trial);
    }

    [Fact]
    public void A_success_from_a_pass_admitted_before_the_open_does_not_close_the_circuit()
    {
        var clock = new ManualClock();
        DecisionCircuitBreaker breaker = Create(clock, new BreakerLogger(), cooldownSeconds: 30);
        breaker.RecordFailure(new HttpRequestException("down"), false, "model", "endpoint");

        breaker.RecordSuccess(trial: false, "model", "endpoint");

        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Skip);
    }

    [Fact]
    public void A_non_service_failure_from_another_pass_does_not_release_the_trial_admission()
    {
        var clock = new ManualClock();
        DecisionCircuitBreaker breaker = Create(clock, new BreakerLogger(), cooldownSeconds: 30);
        breaker.RecordFailure(new HttpRequestException("down"), false, "model", "endpoint");
        clock.Advance(TimeSpan.FromSeconds(30));
        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Trial);

        breaker.RecordFailure(new LocalLlmGateTimeoutException("gate"), false, "model", "endpoint");

        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Skip);
    }

    [Fact]
    public void Non_service_failures_neither_open_nor_close_an_open_circuit()
    {
        var clock = new ManualClock();
        DecisionCircuitBreaker breaker = Create(clock, new BreakerLogger(), cooldownSeconds: 30);
        breaker.RecordFailure(new LocalLlmGateTimeoutException("gate"), false, "model", "endpoint");
        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Closed);

        breaker.RecordFailure(new HttpRequestException("down"), false, "model", "endpoint");
        breaker.RecordFailure(new LocalLlmGateTimeoutException("gate"), false, "model", "endpoint");
        breaker.TryEnter().ShouldBe(DecisionCircuitBreaker.Admission.Skip);
    }

    private static DecisionCircuitBreaker Create(ManualClock clock, BreakerLogger logs, int cooldownSeconds) =>
        new(Options.Create(new ImposterOptions
        {
            DecisionsTools = new DecisionsToolsOptions
            {
                Enabled = true,
                BaseUrl = "https://decision.example",
                CircuitBreakSeconds = cooldownSeconds
            }
        }), clock, logs);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class BreakerLogger : ILogger<DecisionCircuitBreaker>
    {
        public List<(LogLevel Level, string Text)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Events.Add((logLevel, formatter(state, exception)));
    }
}
