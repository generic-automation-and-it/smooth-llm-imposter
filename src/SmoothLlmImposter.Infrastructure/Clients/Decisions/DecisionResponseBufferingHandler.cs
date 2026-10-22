namespace SmoothLlmImposter.Infrastructure.Clients.Decisions;

internal sealed class DecisionResponseBufferingHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
        try
        {
            if (response.IsSuccessStatusCode)
            {
                await response.Content.LoadIntoBufferAsync(cancellationToken);
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
