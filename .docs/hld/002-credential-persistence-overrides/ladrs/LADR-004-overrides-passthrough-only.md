# LADR-004 — Overrides scoped to the passthrough path only

- **Date / Status:** 2026-06-15 · Proposed

## Context

The feature request reads as "store and override keys for both OpenAI and Claude Code." Taken literally that
could mean stored credentials override the keys on **matched imposter** routes too. But HLD 001's imposter
contract is precisely that a matched route rewrites the model and applies the **provider's configured key**
([HLD 001 NFR-002](../../001-llm-imposter-routing/nfrs/NFR-002-credential-security.md)). Letting a database
row silently change which key an imposter route uses would make the hot path's behaviour non-deterministic
from config alone and reintroduce hidden state on the latency-sensitive path.

## Decision

Stored credentials and runtime overrides apply **only to the passthrough / default path** — the branch taken
when no imposter mapping matches and the dialect has a default provider ([HLD 001 LADR-005](../../001-llm-imposter-routing/ladrs/LADR-005-no-default-passthrough-type-only.md)).
**Matched imposter routes are unchanged**: they keep using settings-defined keys and never read the database.

The two requested override behaviours both live on this path:
1. **Active-credential switch** — flip the active stored credential for a dialect at runtime (e.g. Claude Code
   *work* ⇄ *private*) without re-login or redeploy, via `PUT /admin/credentials/{id}/activate`.
2. **Auth-scheme translation** — apply the credential's `AuthScheme` (`ApiKey` ⇄ `Bearer`) when forwarding,
   so a caller's inbound scheme need not match the upstream's required scheme.

## Consequences

- The router gains exactly one new lookup, gated on the existing no-match/passthrough branch — the matched
  branch's code and timing are untouched.
- "Override an imposter route's key" is explicitly **out of scope**; changing an imposter key stays a config
  change. This keeps HLD 001's imposter determinism intact.
- At most one credential per dialect is `IsActive`; activation deactivates siblings transactionally.
