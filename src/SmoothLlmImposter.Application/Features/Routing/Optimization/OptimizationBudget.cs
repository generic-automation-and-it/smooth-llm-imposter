namespace SmoothLlmImposter.Application.Features.Routing.Optimization;

/// <summary>
/// Per-request time budget of the message strategy (HLD 017 Open item 7). The deadline starts with the first
/// model call, so a request that only replays cached messages creates no timer. Expiry cancels the in-flight
/// call through a token linked to the caller's; it is told apart from caller cancellation so it never
/// propagates and never reaches a circuit.
/// </summary>
internal sealed class OptimizationBudget(TimeSpan limit, TimeProvider timeProvider, CancellationToken caller)
    : IDisposable
{
    private CancellationTokenSource? _deadline;
    private CancellationTokenSource? _linked;

    public TimeSpan Limit => limit;

    public int Candidates { get; private set; }

    public int Unprocessed { get; private set; }

    public bool IsExhausted =>
        _deadline is { IsCancellationRequested: true } && !caller.IsCancellationRequested;

    public CancellationToken Start()
    {
        if (_linked is null)
        {
            _deadline = new CancellationTokenSource(limit, timeProvider);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _deadline.Token);
        }

        return _linked.Token;
    }

    public void CountCandidates(int count) => Candidates += count;

    public void CountUnprocessed(int count) => Unprocessed += count;

    public void Dispose()
    {
        _linked?.Dispose();
        _deadline?.Dispose();
    }
}
