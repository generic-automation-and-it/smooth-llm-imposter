# NFR-001: Credential confidentiality and threat boundary

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-20 |

## Requirement

- No plaintext access or refresh token appears in the SQLite database, an API response, a query string, an
  admin example or a log line. Raw device/token HTTP bodies are never logged.
- The client ID is a public identifier and may be returned/persisted; the user code is returned only by the
  authorized setup workflow.
- `message` fields interpolate only already-returned safe fields (`verificationUri`, `userCode`, IDs, provider
  keys, `Retry-After`) — never a token, device code, credential or raw OAuth body.
- Tokens are encrypted with an isolated key ring under the feature data directory; file permissions are
  restricted to the runtime user.

## Threat boundary

Encrypting the database with a key ring stored in the same volume protects an isolated copy of the database. It
does **not** protect against a host/Docker administrator or theft of the whole volume. The feature must not be
described as a vault.

## Verification

L1 proves ciphertext-at-rest and key-ring reopen; L2 asserts responses and messages contain no token or device
code; the AOT smoke run confirms the native artifact writes no plaintext to the database.
