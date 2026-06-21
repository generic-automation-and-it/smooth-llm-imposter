using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using SmoothLlmImposter.Application.Features.ProviderConfiguration;
using SmoothLlmImposter.Host.Configuration;

namespace SmoothLlmImposter.Host.Endpoints;

internal static class ProviderConfigurationEndpoints
{
    public static void MapProviderConfigurationEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/admin/providers")
            .RequireAuthorization(AdminApiKeyAuthenticationHandler.AdminPolicy);

        group.MapGet("/", ListAsync);
        group.MapGet("/{key}", GetAsync);
        group.MapPut("/{key}", UpsertAsync);
        group.MapDelete("/{key}", DeleteAsync);
        group.MapPut("/{key}/enable", EnableAsync);
        group.MapPut("/{key}/disable", DisableAsync);
    }

    private static async Task<Ok<IReadOnlyList<ProviderConfigurationResponse>>> ListAsync(ISender sender, CancellationToken cancellationToken) =>
        TypedResults.Ok(await sender.Send(new ListProviders.Request(), cancellationToken));

    private static async Task<Results<Ok<ProviderConfigurationResponse>, NotFound>> GetAsync(string key, ISender sender, CancellationToken cancellationToken)
    {
        ProviderConfigurationResponse? response = await sender.Send(new GetProvider.Request(key), cancellationToken);
        return response is null ? TypedResults.NotFound() : TypedResults.Ok(response);
    }

    private static async Task<Ok<ProviderConfigurationResponse>> UpsertAsync(
        string key,
        ProviderConfigurationBody body,
        ISender sender,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ProviderConfigurationResponse response = await sender.Send(new UpsertProvider.Request(key, body, Actor(context)), cancellationToken);
        return TypedResults.Ok(response);
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(string key, ISender sender, HttpContext context, CancellationToken cancellationToken)
    {
        bool deleted = await sender.Send(new DeleteProvider.Request(key, Actor(context)), cancellationToken);
        return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static Task<Results<Ok<ProviderConfigurationResponse>, NotFound>> EnableAsync(
        string key,
        ISender sender,
        HttpContext context,
        CancellationToken cancellationToken) =>
        SetEnabledAsync(key, enabled: true, sender, context, cancellationToken);

    private static Task<Results<Ok<ProviderConfigurationResponse>, NotFound>> DisableAsync(
        string key,
        ISender sender,
        HttpContext context,
        CancellationToken cancellationToken) =>
        SetEnabledAsync(key, enabled: false, sender, context, cancellationToken);

    private static async Task<Results<Ok<ProviderConfigurationResponse>, NotFound>> SetEnabledAsync(
        string key,
        bool enabled,
        ISender sender,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ProviderConfigurationResponse? response = await sender.Send(new SetProviderEnabled.Request(key, enabled, Actor(context)), cancellationToken);
        return response is null ? TypedResults.NotFound() : TypedResults.Ok(response);
    }

    private static string? Actor(HttpContext context) => context.User.Identity?.Name;
}
