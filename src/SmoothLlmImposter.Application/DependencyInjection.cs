using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the routing pipeline (catalog, resolver, transformers, router, error factory) and the
    /// startup options validator. Options binding itself is performed by the Host so environment
    /// variables can override <c>appsettings.json</c>.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IProviderCatalog, ProviderCatalog>();
        services.AddSingleton<IRouteResolver, RouteResolver>();
        services.AddSingleton<IRequestTransformer, OpenAiRequestTransformer>();
        services.AddSingleton<IRequestTransformer, AnthropicRequestTransformer>();
        services.AddSingleton<IImposterRouter, ImposterRouter>();
        services.AddSingleton<IErrorResponseFactory, ErrorResponseFactory>();

        services.AddSingleton<IValidateOptions<ImposterOptions>, ImposterOptionsValidator>();

        return services;
    }
}
