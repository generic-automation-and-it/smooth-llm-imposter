using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using SmoothLlmImposter.Application.Features.AuthorizationOverride;
using SmoothLlmImposter.Host.Configuration;

namespace SmoothLlmImposter.Host.Endpoints;

/// <summary>
/// Admin-authed control surface for the per-dialect passthrough authorization override (HLD 003).
/// <c>PUT</c> arms it, <c>DELETE</c> disarms it, <c>GET</c> reports state — all under <see cref="AdminApiKeyAuthenticationHandler.AdminPolicy"/>,
/// unlike the key-less proxy endpoints. No request body or query parameter is read.
/// </summary>
internal static class AuthorizationOverrideEndpoints
{
    public static void MapAuthorizationOverrideEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/routing")
            .RequireAuthorization(AdminApiKeyAuthenticationHandler.AdminPolicy);

        group.MapPut("/{dialect}/override-authorization", PutDefaultAsync);
        group.MapDelete("/{dialect}/override-authorization", DeleteDefaultAsync);
        group.MapGet("/{dialect}/override-authorization", GetDefaultAsync);
        group.MapPut("/{dialect}/{provider}/override-authorization", PutProviderAsync);
        group.MapDelete("/{dialect}/{provider}/override-authorization", DeleteProviderAsync);
        group.MapGet("/{dialect}/{provider}/override-authorization", GetProviderAsync);
    }

    private static Task<Results<Ok<AuthorizationOverrideState>, ProblemHttpResult>> PutDefaultAsync(
        string dialect,
        ISender sender,
        HttpContext context,
        CancellationToken cancellationToken) =>
        PutAsync(dialect, provider: null, sender, context, cancellationToken);

    private static Task<Ok<AuthorizationOverrideState>> DeleteDefaultAsync(
        string dialect,
        ISender sender,
        HttpContext context,
        CancellationToken cancellationToken) =>
        DeleteAsync(dialect, provider: null, sender, context, cancellationToken);

    private static Task<Ok<AuthorizationOverrideState>> GetDefaultAsync(
        string dialect,
        ISender sender,
        CancellationToken cancellationToken) =>
        GetAsync(dialect, provider: null, sender, cancellationToken);

    private static Task<Results<Ok<AuthorizationOverrideState>, ProblemHttpResult>> PutProviderAsync(
        string dialect,
        string provider,
        ISender sender,
        HttpContext context,
        CancellationToken cancellationToken) =>
        PutAsync(dialect, provider, sender, context, cancellationToken);

    private static Task<Ok<AuthorizationOverrideState>> DeleteProviderAsync(
        string dialect,
        string provider,
        ISender sender,
        HttpContext context,
        CancellationToken cancellationToken) =>
        DeleteAsync(dialect, provider, sender, context, cancellationToken);

    private static Task<Ok<AuthorizationOverrideState>> GetProviderAsync(
        string dialect,
        string provider,
        ISender sender,
        CancellationToken cancellationToken) =>
        GetAsync(dialect, provider, sender, cancellationToken);

    private static async Task<Results<Ok<AuthorizationOverrideState>, ProblemHttpResult>> PutAsync(
        string dialect,
        string? provider,
        ISender sender,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SetAuthorizationOverrideResult result = await sender.Send(new SetAuthorizationOverride.Request(dialect, provider, Actor(context)), cancellationToken);

        return result.NoActiveCredential
            ? TypedResults.Problem(
                detail: $"The '{dialect}/{result.State.ProviderName}' provider has no active stored credential to present; the override was not armed.",
                statusCode: StatusCodes.Status403Forbidden)
            : TypedResults.Ok(result.State);
    }

    private static async Task<Ok<AuthorizationOverrideState>> DeleteAsync(
        string dialect,
        string? provider,
        ISender sender,
        HttpContext context,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await sender.Send(new ClearAuthorizationOverride.Request(dialect, provider, Actor(context)), cancellationToken));

    private static async Task<Ok<AuthorizationOverrideState>> GetAsync(
        string dialect,
        string? provider,
        ISender sender,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await sender.Send(new GetAuthorizationOverride.Request(dialect, provider), cancellationToken));

    private static string? Actor(HttpContext context) => context.User.Identity?.Name;
}
