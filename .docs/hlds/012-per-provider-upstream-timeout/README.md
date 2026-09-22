# Per-Provider Upstream Timeout — High-Level Design

| | |
|---|---|
| **Status** | Completed — `TimeoutSeconds` per-provider override live; global base raised to 300 s. LADR-01/02/03 + NFR-01 Accepted. |
| **Owner** | SmoothLlmImposter maintainers |
| **Tracker** | _None — implemented without a tracker issue (NO-TICKET)_ |
| **Last updated** | 2026-09-22 |

> Design HLD. This document delivers **intent + spec** — what we are building and why, the
> decisions behind it, and the quality bar it must meet. It does **not** contain an
> implementation plan.

## TL;DR

`TimeoutSeconds` on a provider sets that provider's **base upstream header timeout**. The
resilience handler scales it across the three attempts — base ×1, ×2, ×3. The default base
moves from 200 s to **300 s**, so the shipped ladder is **300 / 600 / 900 s** and an unset
provider behaves as before except for the longer first attempt. The value bounds only the
wait for *response headers*; once headers arrive, an SSE body streams for as long as the
caller stays connected.

## Intent

Before this HLD the ladder (200/600/900 s) was a hardcoded `static readonly` array in
`Infrastructure/DependencyInjection.cs`, shared by every provider and unreachable from
configuration. That is wrong on two counts:

- **Upstreams differ by an order of magnitude.** A local LM Studio loading a 70B model from
  disk and a hosted gateway answering in under a second sit behind the same named client, so
  one number must serve both. Raising it globally makes a dead upstream hang every route;
  lowering it makes a slow-but-healthy upstream look dead.
- **A hardcoded number cannot be tuned in a deployment.** This router is configured entirely
  through `appsettings` + injected env; a timeout that requires a rebuild is the only routing
  behaviour that could not be adjusted per deployment.

The 200 s first attempt was also the odd one out: it is the wait *before* any retry, so it is
the value most likely to declare a merely-slow upstream dead. 300 s makes the ladder uniform
in shape (×1/×2/×3) and gives the first attempt the same head-room proportion as the retries.

## Key Goals

### 1. Per-provider opt-in, unset = global default

`ProviderOptions.TimeoutSeconds` is a nullable `int`, `null` (default) by default, with the
conventional per-provider env override `<PROVIDER>_TIMEOUT_SECONDS` (HLD 007 LADR-02 surface).

**Acceptance criteria / DoD**

- Binds from structured config and from the conventional env suffix, and survives the HLD 008
  registry seed clone into `ProviderRoute`.
- A provider with it unset gets exactly the 300/600/900 s ladder.
- The `/admin/providers` CRUD surface (HLD 008) reads and writes it, so an upsert of a
  tuned provider does not reset it to the default.

### 2. One knob, ladder shape preserved

A single base scales into the whole ladder rather than replacing one attempt or flattening
all three (LADR-01).

### 3. Fail fast on a nonsense value

`TimeoutSeconds` outside 1–3600 fails startup validation rather than silently falling back
(NFR-01). A `0` that quietly became 300 would leave an operator debugging the wrong thing.

## Non-Goals

- **A total-request or streaming-idle budget.** This bounds time-to-first-byte only; a hung
  mid-stream upstream is HLD-independent and already handled by the terminal-error-frame work.
- **Per-provider retry counts or delays.** The 2-retry / 1 s / 2 s policy stays global.
- **Making the ladder multipliers configurable.** Shape is a product decision, not a knob.

## Decisions

| | |
|---|---|
| [LADR-01](./ladrs/LADR-01-scaled-base-timeout.md) | One base value, scaled ×1/×2/×3 across attempts |
| [LADR-02](./ladrs/LADR-02-request-option-transport.md) | Carry the value on `HttpRequestMessage.Options`, read it from the resilience context |
| [LADR-03](./ladrs/LADR-03-headers-only-scope.md) | Bound response headers only; never the streaming body |
| [NFR-01](./nfrs/NFR-01-fail-fast-range.md) | Validated range, no silent fallback |
