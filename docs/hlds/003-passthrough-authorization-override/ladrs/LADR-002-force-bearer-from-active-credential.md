# LADR-002: ON forces `Bearer` from the active credential, dropping `x-api-key`

**Status:** Accepted

## Context

HLD 002 already consults the dialect's **active** stored credential on the passthrough path and forwards it
using that credential's own `AuthScheme` (`ApiKey` → `x-api-key`, `Bearer` → `Authorization: Bearer`). The new
requirement — "remove the `x-api-key` and use the stored bearer authorization" — is therefore not about
*which* credential is used but about *how its secret is presented*: force `Bearer`, never `x-api-key`, even
when the stored scheme says `ApiKey`.

## Decision

**Force** the `Authorization: Bearer <secret>` scheme on the passthrough branch when the dialect's override is
ON, using the secret of the dialect's **active** credential (HLD 002 `IsActive`, resolved via the existing
`GetActiveAsync` lookup — no new "latest" query). The `x-api-key` header is omitted for that request. When the
override is OFF, forwarding is unchanged from HLD 002 (active credential applied with its own `AuthScheme`, or
the config key when there is no active credential).

The active credential is selected exactly as today, through HLD 002's `activate` flow; this decision changes
only the outbound header scheme, applied at just-in-time forward after decryption.

## Alternatives Considered

- **Filter to credentials whose `AuthScheme` is already `Bearer`** — rejected: the operator wants to force `Bearer` regardless of how the credential was stored; requiring a re-store/rotate to flip scheme defeats the runtime trigger.
- **Add a new "latest stored credential" query (by timestamp)** — rejected: "latest" was clarified to mean the *active* credential; reusing `GetActiveAsync` avoids a second selection concept competing with `IsActive`.
- **Make the switch also choose the credential** — rejected: selection stays HLD 002's `activate` (single source of truth for "which credential").

## Consequences

- A single stored credential can serve both `x-api-key` upstreams (override OFF) and `Bearer`-only upstreams (override ON) without rotation.
- The forwarder gains one conditional on the passthrough branch: "override ON ⇒ Bearer + no `x-api-key`". The matched-imposter branch is untouched (see [LADR-003](LADR-003-passthrough-only-imposter-untouched.md)).
- If the override is ON and there is no active credential at request time, there is nothing to present as `Bearer`; the request fails closed rather than emitting `x-api-key` (see [LADR-005](LADR-005-403-no-active-credential-fail-closed.md)).

## Related

- **LADR-001** — where the ON/OFF state lives.
- **HLD 002 LADR-004** — the passthrough-only credential seam this refines.
