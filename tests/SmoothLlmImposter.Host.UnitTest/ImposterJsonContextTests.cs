using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SmoothLlmImposter.Application.Features.Routing;
using SmoothLlmImposter.Host.Configuration;
using SmoothLlmImposter.Host.Endpoints;

namespace SmoothLlmImposter.Host.UnitTest;

/// <summary>
/// Drift guard for <c>ImposterJsonContext</c> (NativeAOT JSON source generation). The minimal-API
/// request-delegate generator resolves endpoint body/return metadata exclusively through that
/// context; a DTO used by an endpoint but missing from it crashes the AOT host at startup, while
/// the JIT host silently falls back to reflection and never notices. This test fails when a new
/// endpoint DTO lacks metadata.
/// </summary>
public sealed class ImposterJsonContextTests
{
    // Types that flow through endpoint methods but are never JSON-serialized: each entry needs its
    // reason here. A failure naming a NEW type means register it in ImposterJsonContext or justify
    // its exclusion in this set.
    private static readonly HashSet<Type> NeverSerialized = new()
    {
        // Passed between Host helpers and the router; constructed in code, never serialized.
        typeof(RoutePlan),
        // Injected from DI snapshots, never bound from a body.
        typeof(ImposterOptions),
        // Captured from inbound headers at the Host edge, never serialized.
        typeof(CallerHeaders),
    };

    [Fact]
    public void EveryEndpointDto_IsServedByTheJsonContext()
    {
        Type[] containers =
        [
            typeof(CredentialAdminEndpoints),
            typeof(ProviderConfigurationEndpoints),
            typeof(AuthorizationOverrideEndpoints),
            typeof(RoutingEndpoints),
        ];

        var required = new HashSet<Type>();
        var listElements = new HashSet<Type>();
        foreach (Type container in containers)
        {
            foreach (MethodInfo method in container.GetMethods(
                         BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Collect(parameter.ParameterType, required, listElements);
                }

                Collect(method.ReturnType, required, listElements);
            }
        }

        required.ShouldNotBeEmpty("no endpoint DTOs discovered — the method scan itself is broken");

        foreach (Type dto in required)
        {
            JsonTypeInfo? metadata = null;
            try
            {
                metadata = ImposterJsonContext.Default.GetTypeInfo(dto);
            }
            catch (NotSupportedException)
            {
                // GetTypeInfo for an unregistered type under a bare context throws; treat as missing.
            }

            metadata.ShouldNotBeNull(
                $"{dto.FullName} is used by an endpoint but has no metadata in ImposterJsonContext. " +
                "Add a [JsonSerializable] entry, or justify its exclusion in NeverSerialized.");
        }

        foreach (Type element in listElements)
        {
            Type listForm = typeof(IReadOnlyList<>).MakeGenericType(element);
            ImposterJsonContext.Default.GetTypeInfo(listForm).ShouldNotBeNull(
                $"{listForm} is returned by an endpoint but has no metadata in ImposterJsonContext.");
        }
    }

    [Fact]
    public void HealthResponse_KeepsExactWireShape()
    {
        // HealthResponse is bound in a lambda, not a declared handler signature, so the scan above
        // cannot discover it. This assertion guards both its registration and its wire shape with
        // the same options wiring Program.cs uses (Web defaults plus the source-gen chain).
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.TypeInfoResolverChain.Add(ImposterJsonContext.Default);

        string json = JsonSerializer.Serialize(new HealthResponse("ok"), options);

        json.ShouldBe("{\"status\":\"ok\"}");
    }

    private static void Collect(Type type, HashSet<Type> required, HashSet<Type> listElements)
    {
        if (type == typeof(void) || type == typeof(string) || type.IsPrimitive || type.IsEnum)
        {
            return;
        }

        if (type.IsGenericType)
        {
            Type definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
            {
                Collect(type.GetGenericArguments()[0], required, listElements);
                return;
            }

            if (definition == typeof(IReadOnlyList<>) ||
                definition == typeof(IEnumerable<>) ||
                definition == typeof(List<>) ||
                definition == typeof(IList<>))
            {
                Type element = type.GetGenericArguments()[0];
                listElements.Add(element);
                Collect(element, required, listElements);
                return;
            }

            if (definition.FullName?.StartsWith("Microsoft.AspNetCore.Http.HttpResults.", StringComparison.Ordinal) == true)
            {
                foreach (Type argument in type.GetGenericArguments())
                {
                    Collect(argument, required, listElements);
                }

                return;
            }

            if (definition == typeof(Microsoft.Extensions.Options.IOptions<>))
            {
                return;
            }
        }

        if (type.IsArray)
        {
            Collect(type.GetElementType()!, required, listElements);
            return;
        }

        if (type.IsInterface || type.Namespace is null || !type.Namespace.StartsWith("SmoothLlmImposter", StringComparison.Ordinal))
        {
            return;
        }

        if (!NeverSerialized.Contains(type))
        {
            required.Add(type);
        }
    }
}
