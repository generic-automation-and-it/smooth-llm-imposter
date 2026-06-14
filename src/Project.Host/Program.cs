var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.Run();

// Exposed so integration tests can target the entry point via WebApplicationFactory<Program>.
// Add composition (Serilog, OpenAPI/Scalar, health checks, AddApplication/AddInfrastructure,
// endpoint mapping) here as features are implemented.
public partial class Program { }
