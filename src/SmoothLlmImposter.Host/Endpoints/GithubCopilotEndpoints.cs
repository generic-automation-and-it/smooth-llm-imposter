using Mediator;
using Microsoft.AspNetCore.Http.HttpResults;
using SmoothLlmImposter.Application.Features.GithubCopilot;
using SmoothLlmImposter.Host.Configuration;

namespace SmoothLlmImposter.Host.Endpoints;

/// <summary>
/// Ordered, admin-authorized GitHub Copilot workflow (R3). Every response is an envelope with a plain
/// <c>message</c> field that names the next curl step; no endpoint returns a token, ciphertext or GitHub
/// device code. Steps are numbered 01-05 in their summaries so the Scalar page and docs read in order.
/// </summary>
internal static class GithubCopilotEndpoints
{
    public static void MapGithubCopilotEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/admin/github-copilot")
            .RequireAuthorization(AdminApiKeyAuthenticationHandler.AdminPolicy)
            .WithTags("GitHub Copilot");

        group.MapPost("/logins", StartLoginAsync)
            .WithName("github-copilot-01-start-login")
            .WithSummary("01 Start GitHub login")
            .WithDescription("Supplies your own OAuth application client ID and optional prefix; returns the verification URI, user code and the complete URL to poll.")
            .Produces<GithubCopilotLoginStartResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        group.MapPost("/logins/{loginId}/complete", CompleteLoginAsync)
            .WithName("github-copilot-02-complete-login")
            .WithSummary("02 Complete GitHub login")
            .WithDescription("Polls GitHub once, validates the account and Copilot entitlement, saves the credential and publishes its provider pair.")
            .Produces<GithubCopilotLoginCompleteResponse>(StatusCodes.Status200OK)
            .Produces<GithubCopilotLoginCompleteResponse>(StatusCodes.Status202Accepted)
            .Produces<GithubCopilotLoginCompleteResponse>(StatusCodes.Status400BadRequest)
            .Produces<GithubCopilotLoginCompleteResponse>(StatusCodes.Status404NotFound)
            .Produces<GithubCopilotLoginCompleteResponse>(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/connections", ListConnectionsAsync)
            .WithName("github-copilot-03-list-connections")
            .WithSummary("03 List connections")
            .WithDescription("Lists saved connections with their provider keys and readiness so an operator can recover a connection ID after image replacement.")
            .Produces<GithubCopilotConnectionListResponse>(StatusCodes.Status200OK);

        group.MapGet("/connections/{connectionId}/models", DiscoverModelsAsync)
            .WithName("github-copilot-04-discover-models")
            .WithSummary("04 Discover supported models")
            .WithDescription("Lists models the account advertises for the Messages and Responses surfaces. Discovery never creates mappings or enables providers.")
            .Produces<GithubCopilotModelsDiscoveryResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        group.MapGet("/connections/{connectionId}", GetConnectionAsync)
            .WithName("github-copilot-05-verify-connection")
            .WithSummary("05 Verify connection / route readiness")
            .WithDescription("Returns publication and per-provider routing readiness. Route inspection only; use the non-streaming --who? probe for a live smoke test.")
            .Produces<GithubCopilotConnectionStatus>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<Ok<GithubCopilotLoginStartResponse>> StartLoginAsync(
        StartLoginBody body,
        ISender sender,
        CancellationToken cancellationToken)
    {
        GithubCopilotLoginStartResponse response = await sender.Send(
            new StartGithubCopilotLogin.Request(body.Prefix, body.ClientId),
            cancellationToken);

        return TypedResults.Ok(response);
    }

    private static async Task<IResult> CompleteLoginAsync(
        string loginId,
        HttpContext context,
        ISender sender,
        CancellationToken cancellationToken)
    {
        GithubCopilotLoginCompleteResponse response = await sender.Send(
            new CompleteGithubCopilotLogin.Request(loginId),
            cancellationToken);

        if (response.RetryAfterSeconds is int retryAfter)
        {
            context.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return response.Status switch
        {
            GithubCopilotCompleteOutcome.Succeeded => TypedResults.Ok(response),
            GithubCopilotCompleteOutcome.Pending => TypedResults.Json(
                response,
                ImposterJsonContext.Default.GithubCopilotLoginCompleteResponse,
                statusCode: StatusCodes.Status202Accepted),
            GithubCopilotCompleteOutcome.SavedNotPublished or GithubCopilotCompleteOutcome.Retryable => TypedResults.Json(
                response,
                ImposterJsonContext.Default.GithubCopilotLoginCompleteResponse,
                statusCode: StatusCodes.Status503ServiceUnavailable),
            GithubCopilotCompleteOutcome.Failed when response.ErrorCode == "login_not_found" => TypedResults.Json(
                response,
                ImposterJsonContext.Default.GithubCopilotLoginCompleteResponse,
                statusCode: StatusCodes.Status404NotFound),
            _ => TypedResults.Json(
                response,
                ImposterJsonContext.Default.GithubCopilotLoginCompleteResponse,
                statusCode: StatusCodes.Status400BadRequest)
        };
    }

    private static async Task<Ok<GithubCopilotConnectionListResponse>> ListConnectionsAsync(
        ISender sender,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await sender.Send(new ListGithubCopilotConnections.Request(), cancellationToken));

    private static async Task<Results<Ok<GithubCopilotConnectionStatus>, NotFound>> GetConnectionAsync(
        string connectionId,
        ISender sender,
        CancellationToken cancellationToken)
    {
        GithubCopilotConnectionStatus? response = await sender.Send(
            new GetGithubCopilotConnection.Request(connectionId),
            cancellationToken);

        return response is null ? TypedResults.NotFound() : TypedResults.Ok(response);
    }

    private static async Task<Results<Ok<GithubCopilotModelsDiscoveryResponse>, NotFound>> DiscoverModelsAsync(
        string connectionId,
        ISender sender,
        CancellationToken cancellationToken)
    {
        GithubCopilotModelsDiscoveryResponse? response = await sender.Send(
            new DiscoverGithubCopilotModels.Request(connectionId),
            cancellationToken);

        return response is null ? TypedResults.NotFound() : TypedResults.Ok(response);
    }

    public sealed record StartLoginBody(string? Prefix, string? ClientId);
}
