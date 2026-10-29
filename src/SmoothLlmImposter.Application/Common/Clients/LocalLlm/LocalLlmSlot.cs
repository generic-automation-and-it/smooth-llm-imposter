namespace SmoothLlmImposter.Application.Common.Clients.LocalLlm;

/// <summary>
/// The single process-wide slot every local-LLM call takes: tool-filter and message-gate decisions and optimizer
/// calls alike, whatever endpoint or model each uses (HLD 015 LADR-05). These models usually share the operator's
/// machine, where one call at a time is the fastest order. The decision and optimizer gates keep their own wait
/// budgets and timeout types; only the slot is shared.
/// </summary>
public sealed class LocalLlmSlot : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public bool TryEnter() => _semaphore.Wait(0, CancellationToken.None);

    public Task<bool> TryEnterAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _semaphore.WaitAsync(timeout, cancellationToken);

    public Task EnterAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);

    public void Release() => _semaphore.Release();

    public void Dispose() => _semaphore.Dispose();
}
