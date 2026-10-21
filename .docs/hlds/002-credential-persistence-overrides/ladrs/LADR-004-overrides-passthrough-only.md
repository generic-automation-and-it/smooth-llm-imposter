# LADR-004 — Overrides scoped to the passthrough path only

- **Date / Status:** 2026-06-15 · Accepted (amended 2026-10-20 — narrow managed-Copilot exception, see below)

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

## Amendment — managed GitHub Copilot exception (2026-10-20)

HLD 013 adds reserved Copilot provider keys (`github-anthropic`/`github-openai` and `<prefix>-` variants) whose
matched imposter route resolves a managed token and API origin from the feature's in-memory connection snapshot.
This is a **narrow, explicit exception** to "matched imposter routes never read stored state", not a reversal:

- The lookup is only for reserved Copilot keys; every other matched route stays config-key-only and DB-free.
- The token is not a generic credential-store row: it lives in a feature-owned SQLite file with its own Data
  Protection key ring, is hydrated to an immutable snapshot at startup, and is never read on the inference path.
- The generic passthrough override behaviours above (active-credential switch, scheme translation) are unchanged
  and still never apply to matched imposter routes.
- HLD 001's determinism property is preserved for all config-defined providers; for a Copilot route, the
  connection revision is the deterministic source of auth and origin, not a hidden override of a configured key.
