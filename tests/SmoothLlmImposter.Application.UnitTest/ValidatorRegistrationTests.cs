using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace SmoothLlmImposter.Application.UnitTest;

/// <summary>
/// Drift guard for the explicit validator registrations in <c>DependencyInjection.AddApplication</c>.
/// Validators are registered one line per request type (assembly scanning is trim-unsafe and breaks
/// NativeAOT); an unregistered request would skip validation silently, so this test fails when a
/// request gains a <c>Validator</c> without a matching registration line.
/// </summary>
public sealed class ValidatorRegistrationTests
{
    [Fact]
    public void EveryRequestValidator_IsRegisteredInDI()
    {
        Assembly assembly = typeof(DependencyInjection).Assembly;

        List<(Type Validator, Type Request)> mappings = assembly.GetTypes()
            .Where(static type => type is { IsClass: true, IsAbstract: false } && type.Name == "Validator")
            .Select(static type => (
                Validator: type,
                Request: type.GetInterfaces()
                    .FirstOrDefault(static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidator<>))
                    ?.GetGenericArguments()[0]))
            .Where(static mapping => mapping.Request is not null)
            .Select(static mapping => (mapping.Validator, mapping.Request!))
            .ToList();

        mappings.ShouldNotBeEmpty("no IValidator<> implementations found — the assembly scan itself is broken");

        var services = new ServiceCollection();
        services.AddApplication();

        foreach ((Type validator, Type request) in mappings)
        {
            Type serviceType = typeof(IValidator<>).MakeGenericType(request);
            ServiceDescriptor? descriptor = services.FirstOrDefault(d => d.ServiceType == serviceType);
            descriptor.ShouldNotBeNull(
                $"IValidator<{request.Name}> ({validator.FullName}) is not registered. Add an explicit line in DependencyInjection.AddApplication.");
            descriptor!.ImplementationType.ShouldBe(validator);
        }
    }
}
