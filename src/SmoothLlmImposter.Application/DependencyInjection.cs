using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SmoothLlmImposter.Application.Common.Clients.Decisions;
using SmoothLlmImposter.Application.Common.Pipelines;
using SmoothLlmImposter.Application.Features.AuthorizationOverride;
using SmoothLlmImposter.Application.Features.Credentials;
using SmoothLlmImposter.Application.Features.GithubCopilot;
using SmoothLlmImposter.Application.Features.ProviderConfiguration;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Application.Features.Routing.Bridge;
using SmoothLlmImposter.Application.Features.Routing.Decisions;
using SmoothLlmImposter.Application.Features.Routing.Normalization;
using SmoothLlmImposter.Application.Features.Routing.Optimization;

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
        services.AddSingleton<IDecisionToolsFilter, DecisionToolsFilter>();

        // Optimization coordinator (HLD 017 LADR-08): runs registered strategies concurrently
        // and merges their edits. Concrete strategies are registered explicitly by their
        // worktasks (NativeAOT: no assembly scanning).
        services.AddSingleton<IOptimizationCoordinator, OptimizationCoordinator>();

        // Shared decision service (HLD 015): consumers opt in independently; the transport profile is mapped
        // from the existing tool-filter operator surface and validated only while some consumer is opted in.
        services.AddSingleton<IDecisionServiceConsumer, DecisionToolsConsumer>();
        services.AddSingleton<IDecisionServiceConsumer, MessagesConsumer>();
        services.TryAddSingleton<DecisionServiceDemand>();
        services.AddSingleton<IConfigureOptions<DecisionServiceOptions>, DecisionServiceOptionsSetup>();
        services.AddSingleton<IValidateOptions<DecisionServiceOptions>, DecisionServiceOptionsValidator>();
        services.AddHostedService<DecisionServiceStartupCheck>();
        services.AddSingleton<IToolCatalogOrderer, ToolCatalogOrderer>();
        services.AddSingleton<IChatToResponsesTransformer, ChatToResponsesStreamTransformer>();
        // HLD 016 Messages-to-Chat bridge. Stateless per-request mappers; TimeProvider drives the stream
        // timers (ping idle, finish grace).
        services.AddSingleton<AnthropicChatRequestMapper>();
        services.AddSingleton<AnthropicChatResponseMapper>();
        services.AddSingleton<AnthropicChatStreamTranslator>();
        services.AddSingleton<IAnthropicChatBridge, AnthropicChatBridge>();
        services.AddSingleton<IRequestTransformer, OpenAiRequestTransformer>();
        services.AddSingleton<IRequestTransformer, AnthropicRequestTransformer>();
        services.AddScoped<IImposterRouter, ImposterRouter>();
        services.AddScoped<IModelCatalogResponder, OpenAiModelCatalogResponder>();
        services.AddScoped<IAnthropicModelCatalogResponder, AnthropicModelCatalogResponder>();
        // Scoped lifetime: the responder is invoked from the minimal-API request scope,
        // depends on a process-lifetime singleton (ISessionTranslationDictionary) and
        // an ILogger — all of which are DI-permitted in a Scoped registration.
        services.AddScoped<IWhoMessageResponder, WhoMessageResponder>();
        // Process-lifetime session translation dictionary (HLD 010, LADR-06). Singleton so all
        // scopes share the same map — a scoped instance would split the dictionary and break
        // --newsession minting across requests.
        services.AddSingleton<ISessionTranslationDictionary, InMemorySessionTranslationDictionary>();
        services.AddSingleton<IErrorResponseFactory, ErrorResponseFactory>();
        services.AddSingleton<IAuthorizationOverrideSwitch, AuthorizationOverrideSwitch>();

        // Conventional <NAME>_<FIELD> env surface (HLD 007) runs as a post-configure, so it applies
        // before the validator at ValidateOnStart. Business logic stays in Application; the Host only binds.
        //
        // The runtime provider registry is seeded from this env-resolved baseline once at startup; scoped
        // routing consumers read the registry directly so runtime CRUD wins after seeding (HLD 008 LADR-04).
        services.AddSingleton<IPostConfigureOptions<ImposterOptions>, ImposterOptionsPostConfigure>();
        services.AddSingleton<IValidateOptions<ImposterOptions>, ImposterOptionsValidator>();

        // ENABLE_GITHUB_COPILOT / GITHUB_COPILOT_CLIENT_ID mapping (R6/R10); a malformed flag throws here,
        // surfaced at startup by the Host's ValidateOnStart.
        services.AddSingleton<IPostConfigureOptions<GithubCopilotOptions>, GithubCopilotOptionsPostConfigure>();
        services.AddSingleton<IValidateOptions<GithubCopilotOptions>, GithubCopilotOptionsValidator>();

        // Opt-in GitHub Copilot feature (R1-R10). Everything here is inert while the flag is off: the
        // published-connection registry stays empty, login/refresh never run, and no Copilot store is opened.
        // Routing reads only the in-memory snapshot; the SQLite store is touched at startup/login/refresh.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IGithubCopilotConnectionRegistry, InMemoryGithubCopilotConnectionRegistry>();
        services.AddSingleton<IGithubCopilotPrefixGate, GithubCopilotPrefixGate>();
        services.AddSingleton<IGithubCopilotPublisher, GithubCopilotPublisher>();
        services.AddSingleton<IGithubCopilotLoginCoordinator, GithubCopilotLoginCoordinator>();
        services.AddSingleton<IGithubCopilotStartupCoordinator, GithubCopilotStartupCoordinator>();
        services.AddSingleton<IGithubCopilotRefreshCoordinator, GithubCopilotRefreshCoordinator>();
        services.AddHostedService<GithubCopilotRefreshService>();
        services.AddScoped<GithubCopilotConnectionStatusReader>();
        services.AddScoped<GithubCopilotProviderOwnership>();

        services.AddMediator(options =>
        {
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.PipelineBehaviors = [typeof(ValidationPipelineBehavior<,>)];
        });
        // Validators are registered explicitly — one line per request type — instead of
        // AddValidatorsFromAssemblyContaining. Assembly scanning carries
        // RequiresUnreferencedCode and breaks NativeAOT trimming. ValidatorRegistrationTests
        // is the drift guard: it fails when a Request gains a Validator without a line here
        // (an unregistered request would otherwise skip validation silently).
        services.AddScoped<IValidator<CreateCredential.Request>, CreateCredential.Validator>();
        services.AddScoped<IValidator<UpdateCredential.Request>, UpdateCredential.Validator>();
        services.AddScoped<IValidator<ListCredentials.Request>, ListCredentials.Validator>();
        services.AddScoped<IValidator<DeleteCredential.Request>, DeleteCredential.Validator>();
        services.AddScoped<IValidator<GetCredential.Request>, GetCredential.Validator>();
        services.AddScoped<IValidator<ActivateCredential.Request>, ActivateCredential.Validator>();
        services.AddScoped<IValidator<ListProviders.Request>, ListProviders.Validator>();
        services.AddScoped<IValidator<GetProvider.Request>, GetProvider.Validator>();
        services.AddScoped<IValidator<UpsertProvider.Request>, UpsertProvider.Validator>();
        services.AddScoped<IValidator<DeleteProvider.Request>, DeleteProvider.Validator>();
        services.AddScoped<IValidator<SetProviderEnabled.Request>, SetProviderEnabled.Validator>();
        services.AddScoped<IValidator<GetAuthorizationOverride.Request>, GetAuthorizationOverride.Validator>();
        services.AddScoped<IValidator<SetAuthorizationOverride.Request>, SetAuthorizationOverride.Validator>();
        services.AddScoped<IValidator<ClearAuthorizationOverride.Request>, ClearAuthorizationOverride.Validator>();
        services.AddScoped<IValidator<StartGithubCopilotLogin.Request>, StartGithubCopilotLogin.Validator>();
        services.AddScoped<IValidator<CompleteGithubCopilotLogin.Request>, CompleteGithubCopilotLogin.Validator>();
        services.AddScoped<IValidator<ListGithubCopilotConnections.Request>, ListGithubCopilotConnections.Validator>();
        services.AddScoped<IValidator<GetGithubCopilotConnection.Request>, GetGithubCopilotConnection.Validator>();
        services.AddScoped<IValidator<DiscoverGithubCopilotModels.Request>, DiscoverGithubCopilotModels.Validator>();

        return services;
    }
}
