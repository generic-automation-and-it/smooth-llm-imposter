using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using SmoothLlmImposter.Application;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Host.Configuration;
using SmoothLlmImposter.Host.Endpoints;
using SmoothLlmImposter.Infrastructure;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Environment variables override appsettings.json (env wins). The default host already adds them last;
// re-adding here makes the precedence explicit. Providers are keyed by name, so structured overrides are
// name-addressed (Imposter__Providers__opencode-go-openai__Secret=sk-...); the conventional per-provider surface
// (OPENCODE_GO_API_KEY=sk-...) is layered on top by ImposterOptionsPostConfigure and wins (HLD 007).
builder.Configuration.AddEnvironmentVariables();

// Default minimum level is Information (set in code so it holds even with no Serilog config section).
// ReadFrom.Configuration is layered last so the `Serilog` section in appsettings.json / env vars can override
// it — e.g. Serilog__MinimumLevel__Override__SmoothLlmImposter.Routing=Debug to enable the full-request dump.
builder.Host.UseSerilog((context, configuration) =>
    configuration
        .MinimumLevel.Information()
        .ReadFrom.Configuration(context.Configuration)
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

// Process-level last-resort exception logging. The request pipeline (UseExceptionHandler below, plus the
// per-forward try/catch in RoutingEndpoints) only sees exceptions that stay on a request's execution path.
// An exception that escapes on a background/thread-pool thread — e.g. inside the HttpClient connection
// pool's async machinery while building or reusing an upstream handler — bypasses ALL request middleware
// and terminates the process via Environment.FailFast with an opaque "InvokeStub_..ctor" stack that hides
// the real fault. These hooks capture the actual exception before teardown (so the root cause is
// diagnosable) and observe unobserved Task faults, which is the one crash class the process can survive.
var processLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SmoothLlmImposter.Process");

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is Exception ex)
    {
        processLogger.LogCritical(ex, "Unhandled exception escaped to the process (terminating={Terminating})", e.IsTerminating);
    }
    else
    {
        processLogger.LogCritical(
            "Non-exception fault escaped to the process (terminating={Terminating}): {Fault}", e.IsTerminating, e.ExceptionObject);
    }

    // Flush Serilog synchronously — the runtime tears the process down immediately after this callback.
    Log.CloseAndFlush();
};

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    processLogger.LogError(e.Exception, "Unobserved task exception observed and suppressed");
    e.SetObserved();
};

app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.MapImposterEndpoints();
app.MapProviderConfigurationEndpoints();
app.MapCredentialAdminEndpoints();
app.MapAuthorizationOverrideEndpoints();

app.Run();

// Exposed so integration tests can target the entry point via WebApplicationFactory<Program>.
public partial class Program { }
