using System.Net;
using Microsoft.Extensions.Logging;
using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Application.Features.Routing.Optimization;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Optimization;

public sealed class SectionCircuitBreakerTests
{
    [Fact]
    public void One_service_failure_opens_the_circuit_for_the_cooldown()
    {
        var clock = new ManualClock();
        var breaker = new SectionCircuitBreaker("message-gate", clock, new CollectingLogger());

        breaker.RecordFailure(new HttpRequestException("boom", null, HttpStatusCode.BadGateway), false, 180, "m", "http://host");
        breaker.TryEnter().ShouldBe(SectionCircuitBreaker.Admission.Skip);

        clock.Advance(TimeSpan.FromSeconds(179));
        breaker.TryEnter().ShouldBe(SectionCircuitBreaker.Admission.Skip);

        clock.Advance(TimeSpan.FromSeconds(2));
        breaker.TryEnter().ShouldBe(SectionCircuitBreaker.Admission.Trial);
        breaker.TryEnter().ShouldBe(SectionCircuitBreaker.Admission.Skip);
    }

    [Fact]
    public void A_successful_trial_closes_the_circuit_and_a_failed_trial_reopens_it()
    {
        var clock = new ManualClock();
        var breaker = new SectionCircuitBreaker("message-optimizer", clock, new CollectingLogger());
        breaker.RecordFailure(new HttpRequestException("boom"), false, 10, "m", "http://host");
        clock.Advance(TimeSpan.FromSeconds(11));

        breaker.TryEnter().ShouldBe(SectionCircuitBreaker.Admission.Trial);
        breaker.RecordSuccess(true, "m", "http://host");
        breaker.TryEnter().ShouldBe(SectionCircuitBreaker.Admission.Closed);

        breaker.RecordFailure(new HttpRequestException("boom"), false, 10, "m", "http://host");
        clock.Advance(TimeSpan.FromSeconds(11));
        breaker.TryEnter().ShouldBe(SectionCircuitBreaker.Admission.Trial);
        breaker.RecordFailure(new HttpRequestException("boom"), true, 10, "m", "http://host");
        breaker.TryEnter().ShouldBe(SectionCircuitBreaker.Admission.Skip);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(DecisionGateTimeoutException))]
    public void A_non_service_failure_does_not_open_the_circuit(Type exceptionType)
    {
        var breaker = new SectionCircuitBreaker("message-gate", new ManualClock(), new CollectingLogger());

        breaker.RecordFailure(CreateException(exceptionType), false, 180, "m", "http://host");

        breaker.TryEnter().ShouldBe(SectionCircuitBreaker.Admission.Closed);
    }

    [Theory]
    [InlineData(typeof(DecisionServiceTimeoutException))]
    [InlineData(typeof(TaskCanceledException))]
    public void Attempt_timeouts_open_the_circuit(Type exceptionType)
    {
        var breaker = new SectionCircuitBreaker("message-optimizer", new ManualClock(), new CollectingLogger());

        breaker.RecordFailure(CreateException(exceptionType), false, 180, "m", "http://host");

        breaker.TryEnter().ShouldBe(SectionCircuitBreaker.Admission.Skip);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public void Transient_http_statuses_do_not_open_the_circuit(HttpStatusCode status)
    {
        var breaker = new SectionCircuitBreaker("message-gate", new ManualClock(), new CollectingLogger());
        breaker.RecordFailure(new HttpRequestException("transient", null, status), false, 180, "m", "http://host");

        breaker.TryEnter().ShouldBe(SectionCircuitBreaker.Admission.Closed);
    }

    [Fact]
    public void Opening_logs_section_model_endpoint_cause_and_retry_time_without_message_content()
    {
        var logger = new CollectingLogger();
        var breaker = new SectionCircuitBreaker("message-gate", new ManualClock(), logger);

        breaker.RecordFailure(new HttpRequestException("secret content", null, HttpStatusCode.BadGateway), false, 180, "gate-model", "http://host:1234/v1/systemone");

        (LogLevel Level, string Text) entry = logger.Events.Single();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Text.ShouldContain("message-gate");
        entry.Text.ShouldContain("gate-model");
        entry.Text.ShouldContain("http://host:1234/v1/systemone");
        entry.Text.ShouldContain("HTTP 502");
        entry.Text.ShouldContain("no call until");
        entry.Text.ShouldNotContain("secret content");
    }

    [Fact]
    public void Endpoint_description_drops_userinfo_and_query()
    {
        SectionCircuitBreaker.Endpoint("http://user:pass@host:8080/base?token=secret", "/v1/x?q=1")
            .ShouldBe("http://host:8080/base/v1/x");
    }

    private static Exception CreateException(Type type) => type switch
    {
        _ when type == typeof(DecisionGateTimeoutException) => new DecisionGateTimeoutException("gate"),
        _ when type == typeof(DecisionServiceTimeoutException) => new DecisionServiceTimeoutException("timeout"),
        _ => (Exception)Activator.CreateInstance(type)!
    };

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan interval) => _now += interval;
    }

    private sealed class CollectingLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Events.Add((logLevel, formatter(state, exception)));
    }
}
