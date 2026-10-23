using SmoothLlmImposter.Domain.Routing;

namespace SmoothLlmImposter.Application.Features.Routing;

public interface IToolCatalogOrderer
{
    string Order(ApiDialect dialect, string path, string body, CallerHeaders callerHeaders);
}
