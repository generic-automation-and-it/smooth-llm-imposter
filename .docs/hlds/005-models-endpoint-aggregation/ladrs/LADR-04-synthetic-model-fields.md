# LADR-04: Synthetic `Model` field values; recognition in Host, aggregation string-out in Application

**Status:** Draft

<!-- Status lifecycle: Draft → Prototype → Accepted. Tactical (how) — numbered after the strategic LADRs. -->

## Context

Because the list is synthesized rather than relayed (LADR-02), the router must mint each OpenAI
`Model` object's required fields itself — `id`, `object`, `created`, `owned_by` — none of which
come from an upstream. Two things must be decided: what values those non-`id` fields take, and where
in the Clean-Architecture layering the recognition and the synthesis live. The routing feature has
a standing non-negotiable: body work is string-in/string-out in Application; HTTP concerns stay in
Host; Infrastructure is `System.Net.Http` only.

## Decision

**Mint** the fields deterministically and **split** the work along the existing Host/Application
seam:

- `id` = the distinct `to` value (LADR-01).
- `object` = the literal `"model"`; the envelope's `object` = `"list"`.
- `created` = a fixed, configuration-independent constant (e.g. `0`) — **never** a wall-clock value,
  so the response is byte-stable across calls and instances (NFR-01).
- `owned_by` = the name of the provider that declares the mapping. On a duplicate `to` across
  providers, the first declaring provider in catalogue order wins (deterministic, matches the
  first-match-wins convention already used for routing).

Layering: the **Host** recognizes the one case (method + dialect + path, per LADR-03) and, on a
match, asks the **Application** for the discovery JSON **string**, then writes it with a `200` and
`application/json`. The Application owns the aggregation/dedup and returns the serialized body. No
`HttpContext` crosses into Application; no upstream call is made, so Infrastructure is not involved.

## Alternatives Considered

- **`created` = current Unix time** — looks more authentic, but breaks determinism (NFR-01) and
  makes responses non-reproducible across instances. Rejected.
- **`owned_by` = a constant brand (e.g. "smooth-llm-imposter")** — simpler, but discards the useful
  provenance of which provider serves the target. Rejected in favour of the declaring provider name.
- **Aggregate in the Host endpoint directly** — violates the body-work-in-Application non-negotiable
  and would make the logic untestable at L0 without the web host. Rejected.

## Consequences

- Responses are reproducible and diff-stable; tests can assert on exact bytes (NFR-01, NFR-02).
- `owned_by` gives operators a hint of which provider backs each advertised model.
- A small new Application seam exists (discovery-JSON builder) alongside the existing router; it must
  stay string-out and free of HTTP/Infrastructure types.

## Related

- **LADR-01** — supplies the `id` source (`to`).
- **LADR-02** — this LADR details the synthesis LADR-02 mandates.
- **LADR-03** — supplies the recognition predicate the Host applies.
