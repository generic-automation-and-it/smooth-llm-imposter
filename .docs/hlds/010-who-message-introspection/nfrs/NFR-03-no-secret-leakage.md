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
appears in any configured `Secret`.

Substring threshold: **≥ 4 characters.** Secrets shorter than 4 characters are too
short to identify uniquely (typical API-key formats are 20+ chars; the 4-char floor
is the smallest substring that a masked-log test can still exercise without the
assertion collapsing into "the whole secret is absent, period"). Tests ship with
secrets of length ≥ 8 to keep the search space tractable; the 4-char floor is a
defensive lower bound, not a statement about expected secret lengths.

## Acceptance Criteria

- L0 test `WhoMessageResponderTests` covers every `(AuthScheme, HasSecret, Override)`
  tuple and passes the substring-leak assertion.
- The responder's only dependency on credentials is `ImposterRouter.DescribeAuth`'s
  scheme-name return value; it never reads `Secret` or `CredentialOverride.Secret`
  directly.
- L0 test asserts the responder does not emit its own reply body to the
  `SmoothLlmImposter.Routing` log at Information or Debug level.
- L2 test captures the host's Serilog sink during a matched-probe request and asserts
  the probe reply's content text is absent from every captured log event.

## Applies To

- Goal 2 (Dialect fidelity) — the envelope shape is correct but contains only
  safe fields.
- [LADR-03](../ladrs/LADR-03-response-envelope.md) — the envelope design enforces
  this NFR by construction.
