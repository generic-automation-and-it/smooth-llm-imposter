using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest.Routing;

public sealed class SessionScopedMemoryTests
{
    [Fact]
    public void Scopes_hold_independent_values()
    {
        SessionScopedMemory<List<string>> memory = Create(new ManualClock());
        memory.Use("one", values => values.Add("a"));

        memory.Use("two", values => values.Count).ShouldBe(0);
        memory.Use("one", values => values.Single()).ShouldBe("a");
    }

    [Fact]
    public void Idle_scope_expires_and_recent_use_keeps_it_alive()
    {
        var clock = new ManualClock();
        SessionScopedMemory<List<string>> memory = Create(clock);
        memory.Use("active", values => values.Add("a"));
        memory.Use("idle", values => values.Add("a"));
        clock.Advance(SessionScopedMemory<List<string>>.IdleLifetime - TimeSpan.FromMinutes(1));
        memory.Use("active", values => values.Count);

        clock.Advance(TimeSpan.FromMinutes(1));

        memory.Use("idle", values => values.Count).ShouldBe(0);
        memory.Use("active", values => values.Count).ShouldBe(1);
    }

    [Fact]
    public void Capacity_evicts_the_least_recently_used_scope()
    {
        var clock = new ManualClock();
        SessionScopedMemory<List<string>> memory = Create(clock);
        memory.Use("refreshed", values => values.Add("a"));
        for (int index = 0; index < SessionScopedMemory<List<string>>.MaxScopes - 1; index++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            memory.Use($"scope-{index}", values => values.Add("a"));
        }
        clock.Advance(TimeSpan.FromMilliseconds(1));
        memory.Use("refreshed", values => values.Count);
        clock.Advance(TimeSpan.FromMilliseconds(1));

        memory.Use("overflow", values => values.Count);

        memory.Use("scope-0", values => values.Count).ShouldBe(0);
        memory.Use("refreshed", values => values.Count).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_use_of_one_scope_is_serialized()
    {
        SessionScopedMemory<List<int>> memory = new(TimeProvider.System, () => []);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(index =>
            Task.Run(() => memory.Use("scope", values => values.Add(index)))));

        memory.Use("scope", values => values.Order().ToArray()).ShouldBe(Enumerable.Range(0, 200).ToArray());
    }

    private static SessionScopedMemory<List<string>> Create(TimeProvider clock) => new(clock, () => []);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan interval) => _now += interval;
    }
}
