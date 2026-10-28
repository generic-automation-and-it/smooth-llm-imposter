using System.Text.Json.Nodes;

namespace SmoothLlmImposter.Application.Common.Clients.Decisions;

public interface IDecisionModelClient
{
    /// <summary>
    /// Sends one decision request through <paramref name="section"/>'s resolved profile and the admission gate
    /// of that profile's endpoint and model (HLD 015 LADR-05).
    /// </summary>
    Task<JsonObject> DecideAsync(string section, JsonObject request, CancellationToken cancellationToken);
}
