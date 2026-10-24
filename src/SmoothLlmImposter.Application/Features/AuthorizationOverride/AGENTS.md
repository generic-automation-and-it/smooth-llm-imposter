# Authorization Override — Feature Context

## TL;DR

Runtime, in-memory, per-provider switch (HLD 003 amended by HLD 008) that forces the **passthrough** path to
present the active stored credential as `Authorization: Bearer`, dropping `x-api-key`. Default OFF, never
persisted, OFF on restart. Dialect-only admin calls resolve to the dialect's enabled default provider. The
forwarding behaviour it gates lives in `Features/Routing/` (`ImposterRouter`, `UpstreamForwarder`).

## Non-Negotiables

- **Read the switch only on the passthrough seam.** `IsEnabled` is consulted exactly once, in
  `ImposterRouter.ResolvePassthroughCredentialAsync`. Never read it (or the credential store) on the matched
  imposter branch — that is the wrong feature (HLD 003 LADR-003).
- **Never persist the switch.** It is an in-memory singleton keyed by `(ApiDialect, providerName)`. No
  entity, column, or migration (LADR-001).
- **Fail closed.** Arm-time: `Set` returns `NoActiveCredential` (→ endpoint 403) when no active credential
  exists for the addressed provider; the switch stays OFF. Request-time fail-closed (403 dialect-shaped)
  lives in `ImposterRouter`, not here (LADR-005). Never fall back to `x-api-key`.
- **One secret-free audit line per toggle.** `Set`/`Clear` each emit exactly one `Information` line
  (actor + dialect + action). This is an HLD-mandated (NFR-003) exception to the "Information = start/end only"
  logging default. Never log the secret.
- **Admin-only control surface.** The `PUT`/`DELETE`/`GET /routing/{dialect}/{provider}/override-authorization`
  endpoints and dialect-only fallback require `AdminPolicy`; the proxy `/v1/*` endpoints stay key-less
  (LADR-004 / NFR-002).

## Key Behaviors

- `Set` distinguishes `Armed` vs `NoActiveCredential` via `SetAuthorizationOverrideResult`; the Host maps the
  latter to a `403` admin-shaped problem detail. `Clear` is idempotent. `Get` reports state including
  `ProviderName`.
- Validators reject unknown `{dialect}` via `ApiDialectParser.TryParse` → 400 through the validation pipeline,
  mirroring `CreateCredential.Validator`. Validators are auto-registered by the assembly scan in
  `Application/DependencyInjection.cs`.
- The switch is registered **Singleton**; a fresh container reads OFF for every provider.

## Test References

- **L0** `Application.UnitTest/AuthorizationOverride` — switch default-OFF + per-dialect isolation; Set/Clear/Get
  handlers (incl. `NoActiveCredential`); validators.
- **L0** `Application.UnitTest/Routing/ImposterRouterTests` — ON ⇒ `ForceBearer`, ON+no-credential ⇒ 403, imposter
  never reads the switch.
- **L0** `Infrastructure.UnitTest/Routing/UpstreamForwarderTests` — `ForceBearer` wire behaviour.
- **L2** `Host.IntegrationTest/AuthorizationOverrideIntegrationTests` — auth, arm/disarm, fail-closed, imposter parity.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-17 | Initial HLD 003 implementation: in-memory per-dialect force-Bearer switch, admin toggle endpoints, fail-closed. | HLD 003 |
| 2026-06-21 | HLD 008 Phase 2: override is provider-addressable; dialect-only calls resolve to the default provider. | #50 |
