using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Common.Clients.Decisions;

public interface IDecisionModelClient
{
    Task<JsonObject> DecideAsync(JsonObject request, CancellationToken cancellationToken);
}
