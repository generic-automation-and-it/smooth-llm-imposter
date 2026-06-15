using Serilog;
using SmoothLlmImposter.Application;
using SmoothLlmImposter.Application.Features.Routing;
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
builder.Services.AddInfrastructure();

WebApplication app = builder.Build();

app.UseSerilogRequestLogging();
app.MapImposterEndpoints();

app.Run();

// Exposed so integration tests can target the entry point via WebApplicationFactory<Program>.
public partial class Program { }
