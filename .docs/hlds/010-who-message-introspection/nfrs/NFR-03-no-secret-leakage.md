# NFR-03: No secret leakage

**Status:** Accepted

## Requirement

The synthetic probe reply **never** contains, in any field — including the content
text, the `model` field, `id`, or any custom extension field — any of:

- A provider `Secret` (raw or masked).
- A stored credential (HLD 008) or credential id.
- A provider `BaseUrl`.
- A provider key or `Name` (the internal registry key).
- The caller's inbound `Authorization` / `x-api-key` value.

The reply contains only: the inbound `model` (which the caller already supplied),
the resolved upstream `TargetModel` (a string the upstream would see in its own
request), and the auth **scheme** name (`Bearer` / `ApiKey` / `caller-passthrough` /
`none`) — exactly the same tokens already written to the Information-level routing
log.

## Verification

Unit tests on `WhoMessageResponder` feed every provider-config permutation (all auth
schemes, all credential-override combinations, secret-bearing and secret-empty) and
assert the response string does not contain any value from the input secret set.
The test fixture also asserts the response contains no substring of length ≥ 4 that
appears in any configured `Secret` — the lower bound is needed because typical API
key formats cannot be uniquely identified below that length, while the test fixture
ships with keys of length ≥ 8 to keep the search space tractable (a documented
limitation: secrets shorter than 8 chars rely solely on exact-match, not
substring-leak, detection).

## Acceptance Criteria

- L0 test `WhoMessageResponderTests` covers every `(AuthScheme, HasSecret, Override)`
  tuple and passes the substring-leak assertion.
- The responder's only dependency on credentials is `ImposterRouter.DescribeAuth`'s
  scheme-name return value; it never reads `Secret` or `CredentialOverride.Secret`
  directly.
- A L0 test (`WhoMessageResponderTests`) asserts the probe does not log its own
  reply body at Information or Debug level.
- A L2 test (`WhoMessageIntegrationTests`) captures the host's log sink and
  asserts the probe reply text is absent.

## Applies To

- Goal 2 (Dialect fidelity) — the envelope shape is correct but contains only
  safe fields.
- [LADR-03](../ladrs/LADR-03-response-envelope.md) — the envelope design enforces
  this NFR by construction.
