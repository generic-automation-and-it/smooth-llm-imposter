using Microsoft.AspNetCore.Authentication;
using Serilog;
using SmoothLlmImposter.Application;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Host.Configuration;
using SmoothLlmImposter.Host.Endpoints;
using SmoothLlmImposter.Infrastructure;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Environment variables override appsettings.json (env wins). The default host already adds them last;
// re-adding here makes the precedence explicit, e.g. Imposter__Providers__0__ApiKey=sk-...
builder.Configuration.AddEnvironmentVariables();

builder.Host.UseSerilog((_, configuration) =>
    configuration
        .MinimumLevel.Information()
        .Enrich.FromLogContext()
        .WriteTo.Console());

builder.Services
    .AddOptions<ImposterOptions>()
    .Bind(builder.Configuration.GetSection(ImposterOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services
    .AddAuthentication(AdminApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, AdminApiKeyAuthenticationHandler>(AdminApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(options =>
    options.AddPolicy(AdminApiKeyAuthenticationHandler.AdminPolicy, policy =>
        policy.RequireRole("CredentialAdmin")));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ValidationExceptionHandler>();

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.MapImposterEndpoints();
app.MapCredentialAdminEndpoints();

app.Run();

// Exposed so integration tests can target the entry point via WebApplicationFactory<Program>.
public partial class Program { }
