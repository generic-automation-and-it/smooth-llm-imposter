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
        RouteGroupBuilder group = app.MapGroup("/routing/{dialect}/override-authorization")
            .RequireAuthorization(AdminApiKeyAuthenticationHandler.AdminPolicy);

        group.MapPut("/", PutAsync);
        group.MapDelete("/", DeleteAsync);
        group.MapGet("/", GetAsync);
    }

    private static async Task<Results<Ok<AuthorizationOverrideState>, ProblemHttpResult>> PutAsync(
        string dialect,
        ISender sender,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        SetAuthorizationOverrideResult result = await sender.Send(new SetAuthorizationOverride.Request(dialect, Actor(context)), cancellationToken);

        return result.NoActiveCredential
            ? TypedResults.Problem(
                detail: $"The '{dialect}' dialect has no active stored credential to present; the override was not armed.",
                statusCode: StatusCodes.Status403Forbidden)
            : TypedResults.Ok(result.State);
    }

    private static async Task<Ok<AuthorizationOverrideState>> DeleteAsync(
        string dialect,
        ISender sender,
        HttpContext context,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await sender.Send(new ClearAuthorizationOverride.Request(dialect, Actor(context)), cancellationToken));

    private static async Task<Ok<AuthorizationOverrideState>> GetAsync(
        string dialect,
        ISender sender,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await sender.Send(new GetAuthorizationOverride.Request(dialect), cancellationToken));

    private static string? Actor(HttpContext context) => context.User.Identity?.Name;
}
