# NFR-03: Consumer-call isolation

**Status:** Accepted

## Requirement

Sharing the client introduces zero cross-call state or answer leakage. Each outbound request contains only that call's supplied state/questions and resolved model. Question keys and all returned answer fields preserve JSON value semantics. Cancellation or failure in one consumer cannot alter another consumer's request, answer or policy. Caller inference headers and credentials are never inherited by decision calls.

## Verification

Use two consumer fixtures with distinct state sentinels, overlapping question keys, different model selections and controlled envelopes. Interleave successful, malformed and cancelled calls; capture outbound messages and returned results. Include unknown answer fields and missing requested keys.

## Acceptance Criteria

- No request or result contains another call's sentinel data.
- The integration does not mutate supplied state/questions or synthesize missing answers.
- Unknown answer fields survive; absent keys remain absent.
- No inbound authentication/identity header reaches the decision service.

## Applies To

Consumer-neutral contract and shared client lifetime; Goals 1 and 2.
