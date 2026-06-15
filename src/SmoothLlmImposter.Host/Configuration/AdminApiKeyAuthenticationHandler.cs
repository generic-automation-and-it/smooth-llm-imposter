using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace SmoothLlmImposter.Host.Configuration;

internal sealed class AdminApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AdminApiKey";
    public const string AdminPolicy = "CredentialAdmin";
    private const string HeaderName = "X-Admin-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string? supplied = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(supplied))
        {
            return Task.FromResult(AuthenticateResult.Fail("Missing admin API key."));
        }

        string? adminKey = configuration["Admin:ApiKey"];
        string? operatorKey = configuration["Admin:OperatorApiKey"];

        if (!string.IsNullOrEmpty(adminKey) && supplied == adminKey)
        {
            return Task.FromResult(AuthenticateResult.Success(CreateTicket(isAdmin: true)));
        }

        if (!string.IsNullOrEmpty(operatorKey) && supplied == operatorKey)
        {
            return Task.FromResult(AuthenticateResult.Success(CreateTicket(isAdmin: false)));
        }

        return Task.FromResult(AuthenticateResult.Fail("Invalid admin API key."));
    }

    private AuthenticationTicket CreateTicket(bool isAdmin)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "api-key-operator"),
            new(ClaimTypes.Name, "api-key-operator")
        };

        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "CredentialAdmin"));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
    }
}
