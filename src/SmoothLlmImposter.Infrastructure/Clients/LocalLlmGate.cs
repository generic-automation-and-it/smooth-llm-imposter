using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.LocalLlm;

namespace SmoothLlmImposter.Infrastructure.Clients;

/// <summary>
/// The single process-wide slot every decision and optimizer call takes (HLD 015 LADR-05), so a local machine
/// serving both models handles them strictly one at a time. A caller holds its admission for the whole call,
/// retries and retry delays included.
/// </summary>
internal sealed class LocalLlmGate(IOptions<LocalLlmGateOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<Admission> EnterAsync(CancellationToken cancellationToken)
    {
        if (!await _semaphore.WaitAsync(TimeSpan.FromSeconds(options.Value.WaitSeconds), cancellationToken))
        {
            throw new LocalLlmGateTimeoutException("The local LLM gate did not admit the call within its wait budget.");
        }

        return new Admission(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    internal readonly struct Admission(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
