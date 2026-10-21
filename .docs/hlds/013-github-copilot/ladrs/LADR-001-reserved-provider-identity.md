# LADR-001: Reserved provider identity is feature-owned

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-20 |
| **Deciders** | SmoothLlmImposter maintainers |

## Context

A Copilot connection is one GitHub account supplying two protocol providers. If those providers were ordinary
`/admin/providers` entries, an operator could create one without a connection, point its `BaseUrl` elsewhere,
change its dialect, or delete it while credentials still exist — leaving a provider claiming to be Copilot that
routes somewhere else, or a saved token with no provider to use it. The token is also invisible to the admin
surface, so the provider definition cannot be validated against a supplied secret.

## Decision

`github-anthropic`, `github-openai` and their `<prefix>-` variants are **reserved identities** recognized by an
exact terminal key suffix in Domain. The feature owns creation and identity: generic CRUD

- refuses to create a missing reserved key (409),
- on an existing key allows `Models` and `Enabled` edits but rejects a change to `Dialect`, the effective
  `AuthScheme=Bearer`, `AuthHeader`, `BaseUrl`, `IsDefault` (and Chat/normalization/encryption-strip options),
- refuses deletion (409) while a durable connection row exists for the prefix — including an expired or
  reauthentication-required row — and allows orphan cleanup only when no row exists.

Row existence and deletion run inside the same per-prefix gate used by login commit/publication, and the
registry lock is never held across awaited database I/O.

## Consequences

- A provider claiming Copilot identity always corresponds to a feature-managed connection, and a token always
  has its pair.
- Operators lose the ability to seed a reserved provider through generic CRUD; configuration seeding is allowed
  only when it satisfies the same fixed identity.
- Delete is not a logout. Removing saved credentials is a volume-level action (LADR-003).
- The ownership index is the durable row, so an expired connection still blocks deletion — intentional, because
  the operator may still want to reauthorize it.
