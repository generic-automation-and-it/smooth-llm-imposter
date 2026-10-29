using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SmoothLlmImposter.Application.Features.Routing;

namespace SmoothLlmImposter.Application.UnitTest;

/// <summary>
/// Drift guard for process memory held by Application singletons. Per-session state must go through
/// <see cref="SessionScopedMemory{T}"/> (bounded, hashed keys, idle expiry); a singleton that grows its own
/// collection fails here until it either uses that type or is added to <see cref="Reviewed"/> with a reason.
/// </summary>
public sealed class ProcessMemoryRegistrationTests
{
    private static readonly Dictionary<string, string> Reviewed = new(StringComparer.Ordinal)
    {
        ["AuthorizationOverrideSwitch._enabled"] = "keyed by dialect + provider; bounded by configured providers",
        ["DecisionAnswerCache._entries"] = "process-wide classifier memo, capped at 4,096 entries (HLD 014 LADR-07)",
        ["DecisionAnswerCache._order"] = "eviction queue of the capped classifier memo",
        ["GithubCopilotLoginCoordinator._flows"] = "admin-started login flows, purged on expiry (HLD 013)",
        ["GithubCopilotPrefixGate._gates"] = "one gate per Copilot provider prefix",
        ["InMemoryGithubCopilotConnectionRegistry._byPrefix"] = "published connection per Copilot prefix (HLD 013)",
        ["InMemoryProviderRegistry._providers"] = "runtime provider registry (HLD 008)",
        ["InMemorySessionTranslationDictionary._map"] = "per-session but intentionally unbounded: HLD 010 NFR-04",
        ["OptimizerAdmissionGate._gates"] = "one gate per optimizer endpoint + model; bounded by the configured transport (HLD 017 LADR-03)"
    };

    private static readonly Type[] MutableCollections =
    [
        typeof(Dictionary<,>), typeof(ConcurrentDictionary<,>), typeof(HashSet<>), typeof(List<>),
        typeof(ConcurrentQueue<>), typeof(ConcurrentBag<>), typeof(Queue<>), typeof(SortedDictionary<,>)
    ];

    [Fact]
    public void Singleton_collections_are_session_scoped_memory_or_reviewed()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        Assembly application = typeof(DependencyInjection).Assembly;
        Type[] singletons = services
            .Where(descriptor => descriptor.Lifetime == ServiceLifetime.Singleton &&
                descriptor.ImplementationType?.Assembly == application)
            .Select(descriptor => descriptor.ImplementationType!)
            .Distinct()
            .ToArray();
        singletons.ShouldContain(typeof(ToolCatalogOrderer), "the singleton scan itself is broken");

        string[] offenders = singletons
            .SelectMany(type => CollectionFields(type, application, []))
            .Where(field => !Reviewed.ContainsKey(field))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.ShouldBeEmpty(
            "Singleton process memory found. Use SessionScopedMemory<T> for per-session state, or add the field to " +
            "Reviewed with the reason it is bounded or intentionally process-wide.");
    }

    [Fact]
    public void Reviewed_entries_still_exist()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        Assembly application = typeof(DependencyInjection).Assembly;
        string[] current = services
            .Where(descriptor => descriptor.Lifetime == ServiceLifetime.Singleton &&
                descriptor.ImplementationType?.Assembly == application)
            .SelectMany(descriptor => CollectionFields(descriptor.ImplementationType!, application, []))
            .ToArray();

        Reviewed.Keys.Where(field => !current.Contains(field)).ShouldBeEmpty("remove stale Reviewed entries");
    }

    private static IEnumerable<string> CollectionFields(Type type, Assembly application, HashSet<Type> visited)
    {
        if (!visited.Add(type) || IsSessionScopedMemory(type))
        {
            yield break;
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            Type fieldType = field.FieldType;
            if (IsSessionScopedMemory(fieldType))
            {
                continue;
            }

            if (fieldType.IsGenericType && MutableCollections.Contains(fieldType.GetGenericTypeDefinition()))
            {
                yield return $"{type.Name}.{field.Name}";
            }
            else if (fieldType.Assembly == application && fieldType.IsClass)
            {
                foreach (string nested in CollectionFields(fieldType, application, visited))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool IsSessionScopedMemory(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SessionScopedMemory<>);
}
