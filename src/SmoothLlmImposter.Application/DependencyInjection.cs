using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Pipelines;
using SmoothLlmImposter.Application.Features.AuthorizationOverride;
using SmoothLlmImposter.Application.Features.Credentials;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.Features.Routing.Normalization;

namespace SmoothLlmImposter.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the routing pipeline (catalog, resolver, transformers, router, error factory), admin
    /// credential slices, and startup options validator. Options binding itself is performed by the Host.
    /// </summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IProviderRegistry, InMemoryProviderRegistry>();
        services.AddHostedService<ProviderRegistryStartupSeeder>();
        services.AddScoped<IProviderCatalog, ProviderCatalog>();
        services.AddScoped<IRouteResolver, RouteResolver>();
        services.AddSingleton<IRequestNormalizer, CodexToOpenAiSdkNormalizer>();
        services.AddSingleton<IChatToResponsesTransformer, ChatToResponsesStreamTransformer>();
        services.AddSingleton<IRequestTransformer, OpenAiRequestTransformer>();
        services.AddSingleton<IRequestTransformer, AnthropicRequestTransformer>();
        services.AddScoped<IImposterRouter, ImposterRouter>();
        services.AddScoped<IModelCatalogResponder, OpenAiModelCatalogResponder>();
        services.AddScoped<IAnthropicModelCatalogResponder, AnthropicModelCatalogResponder>();
        services.AddSingleton<IErrorResponseFactory, ErrorResponseFactory>();
        services.AddSingleton<IAuthorizationOverrideSwitch, AuthorizationOverrideSwitch>();

        // Conventional <NAME>_<FIELD> env surface (HLD 007) runs as a post-configure, so it applies
        // before the validator at ValidateOnStart. Business logic stays in Application; the Host only binds.
        //
        // Registration ORDER is load-bearing (post-configures run in registration order): the env surface
        // MUST run first so it seeds the baseline, then the registry overlay runs LAST so runtime CRUD wins
        // over env after startup (HLD 008 LADR-04). Reordering these silently breaks runtime-wins — the
        // `Runtime_upsert_wins_over_environment_override_*` integration test guards it.
        services.AddSingleton<IPostConfigureOptions<ImposterOptions>, ImposterOptionsPostConfigure>();
        services.AddSingleton<IPostConfigureOptions<ImposterOptions>, ProviderRegistryOptionsPostConfigure>();
        services.AddSingleton<IValidateOptions<ImposterOptions>, ImposterOptionsValidator>();

        services.AddMediator(options =>
        {
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.PipelineBehaviors = [typeof(ValidationPipelineBehavior<,>)];
        });
        services.AddValidatorsFromAssemblyContaining<CreateCredential.Request>(includeInternalTypes: true);

        return services;
    }
}
