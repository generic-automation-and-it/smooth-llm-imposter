using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using SmoothLlmImposter.Application;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Host.Configuration;
using SmoothLlmImposter.Host.Endpoints;
using SmoothLlmImposter.Infrastructure;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

RegisterProcessFaultDiagnostics();

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

app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();
app.MapImposterEndpoints();
app.MapProviderConfigurationEndpoints();
app.MapCredentialAdminEndpoints();
app.MapAuthorizationOverrideEndpoints();

app.Run();

// Process-level last-resort exception diagnostics. The request pipeline only sees exceptions that stay on a
// request's execution path; fail-fast crashes can bypass middleware and can terminate before structured logs
// flush. These hooks register before host construction and write directly to stderr before using Serilog.
void RegisterProcessFaultDiagnostics()
{
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        if (e.ExceptionObject is Exception ex)
        {
            WriteExceptionToStderr("Unhandled exception escaped to the process", ex, e.IsTerminating);
            Log.Fatal(ex, "Unhandled exception escaped to the process (terminating={Terminating})", e.IsTerminating);
        }
        else
        {
            WriteDetailToStderr(
                "Non-exception fault escaped to the process",
                e.ExceptionObject?.ToString() ?? "(null)",
                e.IsTerminating);
            Log.Fatal(
                "Non-exception fault escaped to the process (terminating={Terminating}): {Fault}",
                e.IsTerminating,
                e.ExceptionObject);
        }

        FlushConsole();
        Log.CloseAndFlush();
    };

    TaskScheduler.UnobservedTaskException += (_, e) =>
    {
        WriteExceptionToStderr("Unobserved task exception observed and suppressed", e.Exception, terminating: false);
        Log.Error(e.Exception, "Unobserved task exception observed and suppressed");
        FlushConsole();
        e.SetObserved();
    };

    if (string.Equals(
        Environment.GetEnvironmentVariable("IMPOSTER_DIAGNOSTIC_FIRST_CHANCE"),
        "true",
        StringComparison.OrdinalIgnoreCase))
    {
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (IsDiagnosticFirstChanceException(e.Exception))
            {
                WriteExceptionToStderr("First-chance diagnostic exception", e.Exception, terminating: false);
                FlushConsole();
            }
        };
    }
}

static bool IsDiagnosticFirstChanceException(Exception exception)
{
    string? source = exception.Source;
    string typeName = exception.GetType().FullName ?? exception.GetType().Name;
    string stack = exception.StackTrace ?? string.Empty;

    return IsDiagnosticNamespace(source) ||
        IsDiagnosticNamespace(typeName) ||
        stack.Contains("System.Net.Http", StringComparison.Ordinal) ||
        stack.Contains("Microsoft.Extensions.Http", StringComparison.Ordinal) ||
        stack.Contains("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal) ||
        stack.Contains("SmoothLlmImposter", StringComparison.Ordinal);
}

static bool IsDiagnosticNamespace(string? value) =>
    value is not null &&
    (value.StartsWith("System.Net.Http", StringComparison.Ordinal) ||
     value.StartsWith("Microsoft.Extensions.Http", StringComparison.Ordinal) ||
     value.StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal) ||
     value.StartsWith("SmoothLlmImposter", StringComparison.Ordinal));

static void WriteExceptionToStderr(string message, Exception exception, bool terminating) =>
    WriteDetailToStderr(message, exception.ToString(), terminating);

static void WriteDetailToStderr(string message, string detail, bool terminating)
{
    Console.Error.WriteLine("[SmoothLlmImposter.Process] {0} (terminating={1})", message, terminating);
    Console.Error.WriteLine(detail);
}

static void FlushConsole()
{
    Console.Error.Flush();
    Console.Out.Flush();
}

// Exposed so integration tests can target the entry point via WebApplicationFactory<Program>.
public partial class Program { }
