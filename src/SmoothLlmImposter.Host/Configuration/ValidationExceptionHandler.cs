using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;

namespace SmoothLlmImposter.Host.Configuration;

/// <summary>
/// Translates FluentValidation <see cref="ValidationException"/> raised by the Mediator validation pipeline
/// into an RFC 7807 validation problem (HTTP 400). Applies uniformly to every endpoint so admin operations
/// fail fast with 400 rather than surfacing a 500. Non-validation exceptions are left to default handling.
/// </summary>
internal sealed class ValidationExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ValidationException validationException)
        {
            return false;
        }

        Dictionary<string, string[]> errors = validationException.Errors
            .GroupBy(failure => failure.PropertyName)
            .ToDictionary(group => group.Key, group => group.Select(failure => failure.ErrorMessage).ToArray());

        await Results.ValidationProblem(errors).ExecuteAsync(httpContext);
        return true;
    }
}
