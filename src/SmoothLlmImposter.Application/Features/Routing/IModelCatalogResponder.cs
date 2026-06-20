namespace SmoothLlmImposter.Application.Features.Routing;

/// <summary>
/// Synthesizes the model-discovery response body from the route catalogue alone — no upstream call,
/// no credential store. String-out so HTTP concerns stay in the Host (HLD 005, LADR-04).
/// </summary>
public interface IModelCatalogResponder
{
    /// <summary>
    /// Builds the OpenAI <c>ListModelsResponse</c> JSON: <c>{ "object": "list", "data": [...] }</c> where
    /// <c>data</c> is one <c>Model</c> per distinct OpenAI-dialect <c>to</c> target in catalogue order.
    /// </summary>
    string BuildOpenAiModelsResponse();
}
