---
description: 'Conventions for Refit-based external API clients: file locations, interface shape, caching adapter, sync (direct) registration'
globs: "**/*.cs"
paths:
  - "**/*.cs"
applyTo: '**/*.cs'
alwaysApply: false
---

# Backend External API Client Conventions

Updated: 2026-05-12

## Non-Negotiables

- **Split list endpoints from singular endpoints.** List queries are not cached; singular (by-id/by-name) queries are cached.
- **List clients** use the plural-noun convention (`IUsersClient`, `IColoursClient`, `ISetsClient`). They live in **`Application/Common/Clients/<Domain>/`**, are Refit interfaces, return `Task<IApiResponse<T>>`, and are registered as raw Refit clients (no caching adapter). Callers (typically the sync feature) check `IsSuccessful` themselves.
- **Singular clients** use the singular-noun convention (`IUserClient`, `ISetClient`). They live in **`Application/Common/Clients/<Domain>/`**, are plain interfaces (no Refit attributes), return `Task<T>` (not `Task<T?>`), and **throw** on errors. The cached adapter is responsible for translating non-success responses into exceptions.
- The Refit interface that backs the singular client (`IXxxApiClient`) lives in **`Infrastructure/Clients/<Domain>/`**, is `internal`, carries the Refit `[Get]` attributes, and returns `Task<IApiResponse<T>>` so the adapter can inspect status codes.
- The caching adapter (`XxxClientCachedAdapter : IXxxClient`) lives in **`Infrastructure/Clients/<Domain>/`** and wraps the Infra-level `IXxxApiClient` via `HybridCache.GetOrCreateAsync`. Inside the factory: **throw `UnknownResourceException` on 404** and **throw on non-success**. The factory throwing means the failure is not cached.
- Registration uses plain `AddRefitClient` + `AddScoped<IXxxClient, XxxClientCachedAdapter>()`. Do not use Scrutor `Decorate<>` and do not register keyed `"sync"` services — list clients are already uncached, singular clients are always cached.

## File Layout

```
Application/
  Common/Clients/<Domain>/
    IXxxClient.cs              ← singular interface (e.g. IUserClient), plain Task<T>, no Refit, throws on error
    IXxxsClient.cs             ← Refit interface for list endpoint(s) (e.g. IUsersClient, IColoursClient), Task<IApiResponse<T>>
    XxxClientOptions.cs        ← optional, e.g. ParallelFetchChunkSize

Infrastructure/
  Clients/<Domain>/
    IXxxApiClient.cs           ← internal Refit interface for singular endpoints, Task<IApiResponse<T>>
    XxxClientCachedAdapter.cs  ← internal sealed : IXxxClient — HybridCache + throws UnknownResourceException on 404
    XxxConverter.cs            ← custom JsonConverter<T> if needed
    XxxOptions.cs              ← options + validator (IValidateOptions<T>)
```

## DI Registration Pattern

```csharp
private static void RegisterXxxClients(IServiceCollection services)
{
    var refitSettings = new RefitSettings { ContentSerializer = ... };

    // List endpoint(s) — Refit, no cache, lives in Application.
    services.AddRefitClient<IXxxsClient>(refitSettings)
        .ConfigureHttpClient(ConfigureXxxBaseAddress)
        .AddStandardResilienceHandler();

    // Singular endpoints — Refit interface in Infrastructure.
    services.AddRefitClient<IXxxApiClient>(refitSettings)
        .ConfigureHttpClient(ConfigureXxxBaseAddress)
        .AddStandardResilienceHandler();

    // Cached singular adapter implements the Application-owned IXxxClient.
    services.AddScoped<IXxxClient, XxxClientCachedAdapter>();
}
```

## Caching Adapter Shape

```csharp
internal sealed class XxxClientCachedAdapter(
    IXxxApiClient apiClient,
    HybridCache cache) : IXxxClient
{
    public Task<SomeDto> GetSomethingAsync(Guid id, CancellationToken ct = default)
        => cache.GetOrCreateAsync(
            key: $"xxx-client:by-id:{id:N}",
            factory: async innerCt =>
            {
                var response = await apiClient.GetSomethingAsync(id, innerCt);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new UnknownResourceException("somethingId", id.ToString());
                }
                if (!response.IsSuccessStatusCode)
                {
                    throw (Exception?)response.Error
                        ?? new InvalidOperationException($"Upstream API non-success: {response.RequestMessage?.RequestUri}");
                }
                return response.Content
                    ?? throw new InvalidOperationException($"Upstream API returned null content for id '{id}'");
            },
            options: ...,
            cancellationToken: ct).AsTask();
}
```

## Sync Usage

The sync feature (`RunUpstreamSyncCycle`) uses:

- **List clients directly** for the bulk fetch stages (uncached, full payload every cycle).
- **Singular clients (cached)** for the parallel per-id fetches (`GetUserByIdAsync`, `GetSetByIdAsync`). Sync pre-warms the cache as a side effect — fresh post-sync requests for those entities hit cache instead of the upstream.

## Why this split?

Caching `IApiResponse<T>` directly breaks `HybridCache` because the underlying `HttpResponseMessage.RequestMessage.Properties` graph contains `RuntimeType` entries that `System.Text.Json` cannot serialize. By caching plain DTOs only (singular clients), the cache stays serializable. The list clients return `IApiResponse<T>` so callers (the sync) can branch on `IsSuccessful`, but those responses are never handed to `HybridCache`.

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change |
|:-----|:-------|
| 2026-05-30 | Initial version. |
