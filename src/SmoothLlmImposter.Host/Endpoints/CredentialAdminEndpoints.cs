using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using SmoothLlmImposter.Application.Features.Credentials;
using SmoothLlmImposter.Host.Configuration;

namespace SmoothLlmImposter.Host.Endpoints;

internal static class CredentialAdminEndpoints
{
    public static void MapCredentialAdminEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/admin/credentials")
            .RequireAuthorization(AdminApiKeyAuthenticationHandler.AdminPolicy);

        group.MapPost("/", CreateAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPut("/{id:guid}", UpdateAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
        group.MapPut("/{id:guid}/activate", ActivateAsync);
    }

    private static async Task<Created<CredentialResponse>> CreateAsync(CreateCredentialBody body, ISender sender, HttpContext context, CancellationToken cancellationToken)
    {
        CredentialMutationResponse response = await sender.Send(new CreateCredential.Request(
            body.ProviderDialect,
            body.Name,
            body.Secret,
            body.AuthScheme,
            body.BaseUrlOverride,
            body.AnthropicVersion,
            Actor(context)), cancellationToken);

        return TypedResults.Created($"/admin/credentials/{response.Credential.Id}", response.Credential);
    }

    private static async Task<Ok<IReadOnlyList<CredentialResponse>>> ListAsync(ISender sender, CancellationToken cancellationToken) =>
        TypedResults.Ok(await sender.Send(new ListCredentials.Request(), cancellationToken));

    private static async Task<Results<Ok<CredentialResponse>, NotFound>> GetAsync(Guid id, ISender sender, CancellationToken cancellationToken)
    {
        CredentialResponse? response = await sender.Send(new GetCredential.Request(id), cancellationToken);
        return response is null ? TypedResults.NotFound() : TypedResults.Ok(response);
    }

    private static async Task<Results<Ok<CredentialResponse>, NotFound>> UpdateAsync(Guid id, UpdateCredentialBody body, ISender sender, HttpContext context, CancellationToken cancellationToken)
    {
        CredentialMutationResponse? response = await sender.Send(new UpdateCredential.Request(
            id,
            body.Name,
            body.AuthScheme,
            body.Secret,
            body.BaseUrlOverride,
            body.AnthropicVersion,
            Actor(context)), cancellationToken);

        return response is null ? TypedResults.NotFound() : TypedResults.Ok(response.Credential);
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(Guid id, ISender sender, HttpContext context, CancellationToken cancellationToken)
    {
        bool deleted = await sender.Send(new DeleteCredential.Request(id, Actor(context)), cancellationToken);
        return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<CredentialResponse>, NotFound>> ActivateAsync(Guid id, ISender sender, HttpContext context, CancellationToken cancellationToken)
    {
        CredentialMutationResponse? response = await sender.Send(new ActivateCredential.Request(id, Actor(context)), cancellationToken);
        return response is null ? TypedResults.NotFound() : TypedResults.Ok(response.Credential);
    }

    private static string? Actor(HttpContext context) => context.User.Identity?.Name;

    public sealed record CreateCredentialBody(
        string ProviderDialect,
        string Name,
        string Secret,
        string AuthScheme,
        string? BaseUrlOverride,
        string? AnthropicVersion);

    public sealed record UpdateCredentialBody(
        string Name,
        string AuthScheme,
        string? Secret,
        string? BaseUrlOverride,
        string? AnthropicVersion);
}
