using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace SmoothLlmImposter.Host.Configuration;

/// <summary>
/// Documents the existing admin API-key scheme in the OpenAPI contract so Scalar shows one authentication
/// affordance for every setup, connection and model operation. The header name matches
/// <see cref="AdminApiKeyAuthenticationHandler"/>; no token is ever placed in a URL.
/// </summary>
internal sealed class AdminApiKeyDocumentTransformer : IOpenApiDocumentTransformer
{
    private const string SchemeId = "X-Admin-Api-Key";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = SchemeId,
            Description = "Admin API key (Admin:ApiKey). Required for every /admin endpoint."
        };

        document.Security ??= [];
        document.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(SchemeId, document)] = []
        });

        return Task.CompletedTask;
    }
}
