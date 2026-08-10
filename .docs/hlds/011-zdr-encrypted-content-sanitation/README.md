# ZDR Encrypted-Content Sanitation — High-Level Design

| | |
|---|---|
| **Status** | Completed — `StripEncryptedContent` per-provider opt-in live. LADR-01/02/03 + NFR-01/02 Accepted. |
| **Owner** | SmoothLlmImposter maintainers |
| **Tracker** | _None — implemented without a tracker issue (NO-TICKET)_ |
| **Last updated** | 2026-08-10 |

> Design HLD. This document delivers **intent + spec** — what we are
> building and why, the decisions behind it, and the quality bar it must meet. It does
> **not** contain an implementation plan; execution (phasing, sub-issues, sequencing) is
> tracked in the issue tracker (worktask list).

## TL;DR

A per-provider opt-in, `StripEncryptedContent`, drops `reasoning` items carrying OpenAI
Zero-Data-Retention `encrypted_content` from the `input` array before the request is
forwarded. The proxy holds no decryption key, so it drops rather than decodes. The option
is independent of `OpenAiUpstreamApi` and `RequestNormalization`, is not gated on
`IsImposter`, and composes before the `chat_completions` downgrade. Default is off.

## Intent

OpenAI's ZDR / stateless mode (`store: false`, or an org-wide Zero Data Retention
setting) returns reasoning output items whose reasoning tokens are carried as ciphertext
in an `encrypted_content` property. Codex replays those items in the `input` array of
every subsequent `/responses` call so reasoning context survives across turns.

An imposter upstream that serves `/responses` but is not OpenAI — LM Studio is the
motivating case — cannot decrypt that ciphertext and rejects the whole request:

```json
{"error":{"message":"Encrypted content is not supported.","type":"BadRequestError","param":null,"code":400}}
```

The decryption key lives only on OpenAI's servers; that is the entire point of ZDR. A
stateless router therefore has exactly one viable move: **drop the ciphertext**. The
upstream does its own thinking and can make no use of the blob. This HLD adds the
narrowest opt-in that makes such an upstream reachable without leaving `/responses`.

## Key Goals

### 1. Per-provider opt-in, default off

`ProviderOptions.StripEncryptedContent` is a nullable `bool`, `null` (off) by default,
with the conventional per-provider env override `<PROVIDER>_STRIP_ENCRYPTED_CONTENT`
(HLD 007 LADR-02 surface). A provider that does not set it behaves exactly as it did
before this HLD.

**Acceptance criteria / DoD**

- `StripEncryptedContent` binds from structured config and from the conventional env
  suffix, and survives the HLD 008 registry seed clone into `ProviderRoute`.
- A provider with the flag unset or `false` forwards reasoning items untouched.
- The `/admin/providers` CRUD surface (HLD 008) reads and writes the flag, so an upsert
  of an opted-in provider does not clear it.

### 2. Drop the item, never decode

A matched item is removed whole. The proxy makes no attempt to decrypt, decode,
truncate, or re-encode `encrypted_content`, and never logs its value.

**Acceptance criteria / DoD**

- No decryption, base64-decode, or cipher-handling code exists on the strip path.
- The stripped payload appears in no log event at any level.

### 3. Scoped strictly to ZDR blocks

Only items in the **top-level `input` array** whose `type` is `reasoning` **and** whose
`encrypted_content` is a non-null JSON node are removed. A plaintext/summary reasoning
item survives verbatim, a JSON-`null` `encrypted_content` is not treated as ciphertext,
and the top-level `reasoning` *config* object (`{"effort":"high"}`) is untouched.

**Acceptance criteria / DoD**

- A reasoning item with only a `summary` survives.
- A reasoning item with `"encrypted_content": null` survives, including its summary.
- `instructions`, a scalar `input`, `messages`, and top-level `reasoning` are unmodified.

### 4. Orthogonal to `OpenAiUpstreamApi` and composable

The strip is a stage in `OpenAiRequestTransformer.Transform` that runs **after**
normalization and **before** `ToChatCompletions`. It therefore applies on the
`responses` forward path (stripped body forwarded as `/responses`) and on the
`chat_completions` downgrade path (strip first, downgrade the survivors).

**Acceptance criteria / DoD**

- `OpenAiUpstreamApi: responses` + flag on → `/responses` body minus ZDR items.
- `OpenAiUpstreamApi: chat_completions` + flag on → ZDR items stripped, remaining
  transcript downgraded to Chat `messages`.
- Session stamping, caching, `RequestNormalization`, and `RejectResponsesStatePointers`
  behave identically with the flag on and off for non-ZDR bodies.

## Core Separation of Concerns

> The strip is a **body-shape** transform, not a routing decision and not a
> normalization profile. It lives entirely inside `OpenAiRequestTransformer`, reads one
> boolean off `ProviderRoute`, and touches nothing else.

`RequestNormalization` (HLD 004) exists to reshape a Codex request into the OpenAI SDK's
Chat Completions tool contract; it is gated on `IsImposter` and defaults on for
`chat_completions`. ZDR sanitation shares none of that: it is an upstream-capability
workaround, meaningful on the `responses` path where normalization is forbidden, and it
must be expressible on a provider regardless of imposter status. Coupling the two would
force operators to accept tool-contract rewriting in order to get ciphertext dropped.

## Guiding Principle — Drop, never decode

> The proxy has no key. Anything that looks like decryption is a bug.

- No key material is read, derived, cached, or configured on this path.
- The ciphertext is discarded, not stored, forwarded, summarized, or logged.
- Dropping is safe for ZDR guarantees; an attempt to decrypt would break both the
  security model and correctness.

---

## Diagrams

- [Transform-stage ordering](./diagrams/c4-context.md)

## Architecture Decisions (LADRs)

LADR-01 and LADR-02 are strategic (*what* and *why*); LADR-03 is tactical (*how*).
See [`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-drop-whole-reasoning-item.md) | Drop the whole reasoning item, not just the `encrypted_content` property | Accepted |
| [LADR-02](./ladrs/LADR-02-independent-per-provider-opt-in.md) | Independent per-provider opt-in — not coupled to `OpenAiUpstreamApi`, `RequestNormalization`, or `IsImposter` | Accepted |
| [LADR-03](./ladrs/LADR-03-no-cross-field-validation.md) | No cross-field validator rule; bad env values follow the existing bool log-and-skip semantics | Accepted |

## Non-Functional Requirements

See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-no-decryption-no-content-logging.md) | Security | No decryption attempt; stripped content never logged | Accepted |
| [NFR-02](./nfrs/NFR-02-default-off-transparency.md) | Transparency | Flag off ⇒ forward path identical to pre-HLD | Accepted |

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-08-09 | Implemented: `StripEncryptedContent` on `ProviderOptions`/`ProviderRoute`, strip stage in `OpenAiRequestTransformer`, conventional env `_STRIP_ENCRYPTED_CONTENT`, 5 L0 tests. | — |
| 2026-08-10 | HLD authored retrospectively (the implementation had cited a non-existent "HLD 011 / LADR-05"). Review fixes folded in: registry-seed clone preserved the flag (it did not, leaving the feature inert), `/admin/providers` CRUD surfaces it, JSON-`null` `encrypted_content` no longer treated as ciphertext, `bool?` added to the cloner drift guard, catalog + L2 round-trip coverage added. LADR-01/02/03 + NFR-01/02 → Accepted; HLD → Completed. | — |
