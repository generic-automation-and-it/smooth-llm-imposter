namespace SmoothLlmImposter.Application.Features.Routing.Decisions;

public interface IDecisionToolsFilter
{
    Task<string> FilterAsync(string path, string body, CancellationToken cancellationToken);
}
