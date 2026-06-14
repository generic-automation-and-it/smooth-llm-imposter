# Testing Strategy

## Test Levels

| Level | Label | Projects | Containers? | Description |
|---|---|---|---|---|
| L0 | Unit | `*.UnitTest` | None | Isolated logic, no I/O — pure in-process |
| L1 | Component | `Application.ComponentTest`, `Infrastructure.ComponentTest` | PostgreSQL + WireMock | End-to-end within a layer; real DB and HTTP stubs via Aspire |
| L2 | Integration | `Host.IntegrationTest` | PostgreSQL + WireMock | Full stack via `WebApplicationFactory` + Aspire containers |

## Test Infrastructure

Shared fixtures live in `tests/Project.TestFramework/`. Container orchestration (PostgreSQL, WireMock) lives in `tests/Project.TestFramework.Aspire/`.

### AspireFixture

`AspireFixture` provisions and shares test containers across all test assemblies in a process. It tries three strategies in order:

1. **Reuse** — if another fixture in the same process already initialised, adopt the shared state
2. **Fixed endpoints** — probe `127.0.0.1:15432` (Postgres) and `127.0.0.1:19091` (WireMock) — succeeds if containers are pre-warmed (CI or local `dotnet run --project tests/Project.TestFramework.Aspire`)
3. **Docker port discovery** — query `docker`/`podman port` for the persistent named containers (`project-test-postgres`, `project-test-wiremock`)
4. **Start Aspire host** — provision fresh containers (takes ~30s on first run)

Container lifetimes are `Persistent` — they survive test runs and are reused on subsequent runs.

### WebAppFixture&lt;T&gt;

Base class for L2 integration tests. Initialises `AspireFixture`, then boots `WebApplicationFactory<TProgram>`. Override hooks:

- `EnrichConfigurationAsync(overrides)` — inject connection strings, WireMock base URL, etc.
- `PostInitializeAsync()` — run post-boot setup (e.g. trigger a sync cycle)
- `RecreateDatabaseOnInitialize` — set `true` to drop/recreate the database before the fixture starts
- `DatabaseName` — default is a Guid-suffixed name for isolation; override for deterministic names

### ProjectTestDatabase

Factory for per-test isolated databases in L1 Infrastructure tests. Drops/recreates a named database and returns a connection string handle. When EF Core migrations are added, extend `CreateAsync` to run migrations before returning.

### DatabaseResetter

Wraps Respawn for fast between-test data wipes without drop/recreate. Use in `IAsyncLifetime.DisposeAsync()` or an `AfterTest` hook.

### WireMockAdminClient

HTTP client for the WireMock admin API. Obtain via `AspireFixture.CreateWireMockAdminClient()`:

```csharp
await using var admin = aspire.CreateWireMockAdminClient();
await admin.StubJsonResponseAsync("GET", "/api/users", new[] { ... });
// ... run test ...
await admin.ResetAsync(); // clear stubs between tests
```

## Container Port Map

| Container | Local Port | Service |
|---|---|---|
| `project-test-postgres` | 15432 | PostgreSQL |
| `project-test-wiremock` | 19091 | WireMock HTTP admin + stubbed endpoints |

## Collection Fixture Pattern

The `"Aspire"` xUnit collection shares one `AspireFixture` instance across all tests in a given assembly. Each L1/L2 test assembly must re-declare the collection:

```csharp
// AspireCollection.cs (in each L1/L2 test project)
[CollectionDefinition("Aspire")]
public sealed class AspireCollection : ICollectionFixture<AspireFixture>;
```

Tests opt in via `[Collection("Aspire")]` and receive `AspireFixture` via constructor injection.

## Running Tests

```bash
# All tests (L0 + L1 + L2) — requires Docker
dotnet test Project.slnx

# L0 only — no containers required
dotnet test tests/Project.Domain.UnitTest
dotnet test tests/Project.Application.UnitTest
dotnet test tests/Project.Infrastructure.UnitTest
dotnet test tests/Project.Host.UnitTest

# L1 component tests
dotnet test tests/Project.Application.ComponentTest
dotnet test tests/Project.Infrastructure.ComponentTest

# L2 integration tests
dotnet test tests/Project.Host.IntegrationTest

# Pre-warm containers (speeds up first test run)
dotnet run --project tests/Project.TestFramework.Aspire
```
