# LADR-02: Two Boundaries — Routing Config vs Credentials

**Status:** Draft

## Context

A provider's configuration includes both routing facts (dialect, base URL, auth scheme, model mappings,
default flag, normalization) and a **secret**. Issue #48 asks for a `GET` that can be copied into a `PUT` to
round-trip configuration, but the project's standing posture (HLD 002, HLD 007 NFR-03) is that secrets are
**never echoed**. These two requirements collide if the secret rides on the same boundary as routing config.

## Decision

**Separate** configuration into two boundaries over the same registry: a **routing-config** boundary that is
**secret-free**, and a **credential** boundary that is **secret-only**. The provider-config API
(`/admin/providers`) reads and writes everything *except* the secret — its `GET` is fully round-trippable
into its `PUT` because the secret is simply not part of that contract. Secrets are read-as-absent and
rotated only through the credential API (see
[LADR-05](./LADR-05-settings-backed-provider-keyed-credentials.md)). A provider-config `PUT` therefore never
clears or alters an existing secret.

## Alternatives Considered

- **Single boundary, secret returned in `GET`** — rejected: exposes secrets over the admin API, violating the
  long-standing no-echo posture and [NFR-02](../nfrs/NFR-02-secret-confidentiality.md).
- **Single boundary, secret masked in `GET` and "keep if omitted" on `PUT`** — rejected by product decision:
  the operator explicitly wanted secrets kept on their own get/put boundary, not interleaved with routing
  config.

## Consequences

- The provider-config `GET`→`PUT` round-trip is clean and safe; no masking sentinel or "omitted means keep"
  ambiguity.
- Operators perform two calls to fully stand up a provider with a secret (config + credential) — a small
  ergonomic cost for a clear security boundary.
- Both boundaries mutate one registry, so they must agree on provider identity (the dictionary key /
  provider name).

## Consequences for the hot path

- None: the forwarder keeps reading the resolved route + credential; only the *management* surface is split.

## Related

- **LADR-05** — the credential boundary's storage and keying.
- Upholds HLD 002 / HLD 007 secret-confidentiality posture; see [NFR-02](../nfrs/NFR-02-secret-confidentiality.md).
