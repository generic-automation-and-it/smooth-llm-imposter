using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
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
// The `Serilog:MinimumLevel` settings in appsettings.json / env vars are applied explicitly below —
// e.g. Serilog__MinimumLevel__Override__SmoothLlmImposter.Routing=Debug enables the full-request dump.
// ReadFrom.Configuration is deliberately NOT used: its assembly scanning finds no assemblies in a
// single-file NativeAOT binary, so the documented level-override surface would silently stop working.
builder.Host.UseSerilog((context, configuration) =>
    ApplySerilogLevels(
        configuration
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console(),
        context.Configuration));

builder.Services.Configure<ImposterOptions>(builder.Configuration.GetSection(ImposterOptions.SectionName));
builder.Services.AddOptions<ImposterOptions>().ValidateOnStart();

// Source-generated JSON metadata for NativeAOT (see ImposterJsonContext): the minimal-API
// RequestDelegateGenerator resolves endpoint body/return types through this chain. No-op
// under JIT, load-bearing under AOT.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Add(ImposterJsonContext.Default));

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

// Applies the `Serilog:MinimumLevel:Default` and `Serilog:MinimumLevel:Override:*` settings in code.
// Unparsable level names are ignored, leaving the code default.
static void ApplySerilogLevels(LoggerConfiguration loggerConfiguration, IConfiguration appConfiguration)
{
    string? defaultLevel = appConfiguration["Serilog:MinimumLevel:Default"];
    if (Enum.TryParse<LogEventLevel>(defaultLevel, ignoreCase: true, out LogEventLevel parsedDefault))
    {
        loggerConfiguration.MinimumLevel.Is(parsedDefault);
    }

    foreach (IConfigurationSection levelOverride in appConfiguration.GetSection("Serilog:MinimumLevel:Override").GetChildren())
    {
        if (!string.IsNullOrWhiteSpace(levelOverride.Key) &&
            Enum.TryParse<LogEventLevel>(levelOverride.Value, ignoreCase: true, out LogEventLevel level))
        {
            loggerConfiguration.MinimumLevel.Override(levelOverride.Key, level);
        }
    }
}

// Exposed so integration tests can target the entry point via WebApplicationFactory<Program>.
public partial class Program { }
