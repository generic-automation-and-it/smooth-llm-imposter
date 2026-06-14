using System.Diagnostics.CodeAnalysis;
using Project.TestFramework.Aspire;

[assembly: ExcludeFromCodeCoverage]

var builder = DistributedApplication.CreateBuilder(args);
builder.AddProjectTestDependencies();

builder.Build().Run();
