using Microsoft.Extensions.Logging;

namespace SmoothLlmImposter.Infrastructure.Routing;

/// <summary>
/// Serializes upstream and GitHub HTTP sends across the whole process behind a single <see cref="SemaphoreSlim"/>,
/// so a stalled upstream cannot be hit by several requests at once. Decision and optimizer clients use
/// <c>LocalLlmGate</c> instead. The semaphore is acquired before the inner send and released when the returned response is
/// disposed (or on failure), which keeps streaming bodies from overlapping with the next outbound call.
/// </summary>
internal sealed class ConcurrencyLimitingHandler(SemaphoreSlim semaphore, ILogger<ConcurrencyLimitingHandler> logger)
    : DelegatingHandler
{
    private readonly SemaphoreSlim _semaphore = semaphore;
    private readonly ILogger<ConcurrencyLimitingHandler> _logger = logger;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Waiting for outbound slot: {Method} {Uri}", request.Method, request.RequestUri);
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("Acquired outbound slot: {Method} {Uri}", request.Method, request.RequestUri);
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            return new ReleasingResponse(response, _semaphore);
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    private sealed class ReleasingResponse : HttpResponseMessage
    {
        private readonly HttpResponseMessage _inner;
        private readonly SemaphoreSlim _semaphore;
        private bool _released;

        public ReleasingResponse(HttpResponseMessage inner, SemaphoreSlim semaphore)
            : base(inner.StatusCode)
        {
            _inner = inner;
            _semaphore = semaphore;
            ReasonPhrase = inner.ReasonPhrase;
            Version = inner.Version;
            RequestMessage = inner.RequestMessage;
            Content = inner.Content;
            foreach (KeyValuePair<string, IEnumerable<string>> header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            if (inner.TrailingHeaders is not null)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> header in inner.TrailingHeaders)
                {
                    TrailingHeaders.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_released)
            {
                _released = true;
                _semaphore.Release();
            }

            _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}