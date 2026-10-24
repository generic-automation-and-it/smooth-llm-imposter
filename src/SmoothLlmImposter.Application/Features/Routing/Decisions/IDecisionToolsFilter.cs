namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

public interface IDecisionToolsFilter
{
    Task<string> FilterAsync(
        string path, string body, CallerHeaders callerHeaders, string inboundBody, CancellationToken cancellationToken);
}
