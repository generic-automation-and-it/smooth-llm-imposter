using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Optimizer;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.Features.Routing.Optimization;

namespace SmoothLlmImposter.Application.UnitTest.Routing.Optimization;

public sealed class MessageChecksumCacheTests
{
    private static readonly SessionIdentity Captured = new("session-1", SessionIdentitySource.Captured);
    private static readonly CallerHeaders CallerHeaders =
        new([new KeyValuePair<string, IReadOnlyList<string>>("authorization", ["Bearer caller"])]);

    [Fact]
    public void First_value_is_final_and_hits_return_the_same_bytes()
    {
        var clock = new ManualClock();
        MessageChecksumCache cache = Create(clock);

        cache.Store(Captured, CallerHeaders, "original", "v1", "optimized");
        cache.Store(Captured, CallerHeaders, "original", "v1", "different");

        cache.TryGet(Captured, CallerHeaders, "original", "v1", out string forwarded).ShouldBeTrue();
        forwarded.ShouldBe("optimized");
    }

    [Fact]
    public void A_new_optimizer_version_misses()
    {
        MessageChecksumCache cache = Create(new ManualClock());
        cache.Store(Captured, CallerHeaders, "original", "v1", "optimized");

        cache.TryGet(Captured, CallerHeaders, "original", "v2", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_captured_session_without_a_value_is_not_cached(string? value)
    {
        MessageChecksumCache cache = Create(new ManualClock());
        var session = new SessionIdentity(value, SessionIdentitySource.Captured);

        cache.Store(session, CallerHeaders, "original", "v1", "optimized");

        cache.TryGet(session, CallerHeaders, "original", "v1", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_derived_session_is_not_cached()
    {
        MessageChecksumCache cache = Create(new ManualClock());
        var session = new SessionIdentity("derived", SessionIdentitySource.Derived);

        cache.Store(session, CallerHeaders, "original", "v1", "optimized");

        cache.TryGet(session, CallerHeaders, "original", "v1", out _).ShouldBeFalse();
    }

    [Fact]
    public void Scopes_are_isolated_by_identity_and_caller_headers()
    {
        MessageChecksumCache cache = Create(new ManualClock());
        var otherSession = new SessionIdentity("session-2", SessionIdentitySource.Captured);
        var otherHeaders = new CallerHeaders(
            [new KeyValuePair<string, IReadOnlyList<string>>("authorization", ["Bearer other"])]);

        cache.Store(Captured, CallerHeaders, "original", "v1", "optimized");

        cache.TryGet(otherSession, CallerHeaders, "original", "v1", out _).ShouldBeFalse();
        cache.TryGet(Captured, otherHeaders, "original", "v1", out _).ShouldBeFalse();
        cache.TryGet(Captured, CallerHeaders, "original", "v1", out string forwarded).ShouldBeTrue();
        forwarded.ShouldBe("optimized");
    }

    [Fact]
    public void Sliding_ttl_drops_a_whole_scope_and_a_hit_refreshes_it()
    {
        var clock = new ManualClock();
        MessageChecksumCache cache = Create(clock, ttlHours: 1);
        cache.Store(Captured, CallerHeaders, "original", "v1", "optimized");

        clock.Advance(TimeSpan.FromMinutes(59));
        cache.TryGet(Captured, CallerHeaders, "original", "v1", out _).ShouldBeTrue();

        clock.Advance(TimeSpan.FromMinutes(2));
        cache.TryGet(Captured, CallerHeaders, "original", "v1", out _).ShouldBeTrue();

        clock.Advance(TimeSpan.FromMinutes(61));
        cache.TryGet(Captured, CallerHeaders, "original", "v1", out _).ShouldBeFalse();
    }

    [Fact]
    public void The_cache_has_no_scope_cap()
    {
        MessageChecksumCache cache = Create(new ManualClock());
        for (int index = 0; index < 300; index++)
        {
            var session = new SessionIdentity($"session-{index}", SessionIdentitySource.Captured);
            cache.Store(session, CallerHeaders, "original", "v1", "optimized");
        }

        cache.TryGet(Captured, CallerHeaders, "original", "v1", out string forwarded).ShouldBeTrue();
        forwarded.ShouldBe("optimized");
    }

    private static MessageChecksumCache Create(TimeProvider clock, int ttlHours = 120) =>
        new(clock, Options.Create(new LlmMessagesOptions { CacheTtlHours = ttlHours }));

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan interval) => _now += interval;
    }
}
