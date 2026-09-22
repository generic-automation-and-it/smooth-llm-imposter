# AGENTS.md - Per-Provider Upstream Timeout

AI Context: HLD for the `TimeoutSeconds` per-provider override (HLD 012). **IMPLEMENTED**. Updated: 2026-09-22

## TL;DR

`TimeoutSeconds` on a provider sets its base upstream **header** timeout; the resilience handler
scales it ×1/×2/×3 across the three attempts. Unset ⇒ the global 300 s base ⇒ the shipped
300/600/900 s ladder. Intent in [README.md](./README.md), decisions in [ladrs/](./ladrs/),
quality spec in [nfrs/](./nfrs/).

## Implementation map

| Concern | Where |
|---|---|
| Option | `ProviderOptions.TimeoutSeconds` (`int?`) — `Features/Routing/ImposterOptions.cs` |
| Env override | `_TIMEOUT_SECONDS`, int path — `ImposterOptionsPostConfigure.cs` |
| Range validation | `ImposterOptionsValidator` (1–3600) + `ProviderConfigurationValidation` body rule |
| Clone (HLD 008 registry) | `ProviderOptionsCloner.Clone` — **must** copy the field |
| Admin CRUD | `ProviderConfigurationResponse` / `ProviderConfigurationBody` — both directions |
| Materialization | `ProviderCatalog` → `ProviderRoute.TimeoutSeconds` |
| Stamp | `UpstreamForwarder.SendAsync` → `request.Options.Set(UpstreamTimeoutSecondsKey, …)` |
| Ladder | `DependencyInjection.GetUpstreamAttemptTimeout` / `GetUpstreamTimeoutSeconds` |
| End-to-end proof | `UpstreamTimeoutPipelineTests` — real pipeline, stalling handler, asserts the retry still carries the stamp |

## Non-Negotiables

- **The forwarder stamp is load-bearing.** Everything else can be wired correctly and the
  feature is still inert without `request.Options.Set(...)` — the exact way HLD 011 shipped
  dead. `UpstreamForwarderTimeoutTests` asserts the stamp and `UpstreamTimeoutPipelineTests`
  drives the seam through the **real** `AddInfrastructure` pipeline with a stalling primary
  handler — both fail loudly if it is removed.
- **A new scalar `ProviderOptions` field must be added to `ProviderOptionsCloner.Clone`.** The
  HLD 008 registry seeder clones every provider before `ProviderCatalog` reads it, so an
  un-cloned field is always `null` on the route. Both drift guards
  (`ProviderOptionsClonerTests`, `ImposterOptionsPostConfigureTests`) now include `int`/`int?`
  — widen them, do not narrow them.
- **Never let a non-positive value reach Polly.** Zero is not "no timeout"; it fails every
  attempt instantly and reads as a dead upstream. The validator rejects it at startup and
  `GetUpstreamTimeoutSeconds` backstops it.
- **Do not repurpose this into a total-request or idle timeout** (LADR-03). It bounds the wait
  for response headers; the client keeps `Timeout.InfiniteTimeSpan` so SSE bodies stream freely.
- **Do not give each provider its own named `HttpClient`** (LADR-02). Providers are
  runtime-mutable (HLD 008); a provider created after startup would have no registered client.
- **Keep `ROUTING_AGENTS.md`, the HLD 007 LADR-02 env table, and `setup.md` in sync** with
  `ImposterOptionsPostConfigure.Fields` when a suffix is added — that table has drifted before.

## Defaults

Shipped in no `appsettings*.json`: the option is unset everywhere and injected per deployment
(`<PROVIDER>_TIMEOUT_SECONDS=…`). The 300 s base lives in
`DependencyInjection.DefaultUpstreamTimeoutSeconds`.
