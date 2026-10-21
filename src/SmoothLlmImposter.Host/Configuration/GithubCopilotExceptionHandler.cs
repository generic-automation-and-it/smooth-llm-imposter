using Microsoft.AspNetCore.Diagnostics;
using SmoothLlmImposter.Application.Features.GithubCopilot;

namespace SmoothLlmImposter.Host.Configuration;

/// <summary>
/// Maps the GitHub Copilot workflow's deliberate failure contracts onto HTTP ProblemDetails: a reserved-provider
/// ownership collision is 409, a reserved-key mutation while the feature is off is 400, and a retryable GitHub
/// call failure is 502. Non-Copilot exceptions are left to default handling.
/// </summary>
internal sealed class GithubCopilotExceptionHandler(ILogger<GithubCopilotExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        (int status, string title)? mapping = exception switch
        {
            GithubCopilotProviderConflictException => (StatusCodes.Status409Conflict, "GitHub Copilot provider conflict"),
            GithubCopilotFeatureDisabledException => (StatusCodes.Status400BadRequest, "GitHub Copilot feature disabled"),
            GithubCopilotUpstreamException => (StatusCodes.Status502BadGateway, "GitHub Copilot upstream failure"),
            _ => null
        };

        if (mapping is not { } mapped)
        {
            return false;
        }

        if (mapped.status == StatusCodes.Status502BadGateway)
        {
            logger.LogWarning(exception, "GitHub Copilot upstream call failed");
        }

        await TypedResults.Problem(
                detail: exception.Message,
                statusCode: mapped.status,
                title: mapped.title)
            .ExecuteAsync(httpContext);

        return true;
    }
}
