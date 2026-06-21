# NFR-03: Performance — no extra full-body pass

**Status:** Accepted

<!-- One file per quality attribute. Horizontal concern spanning the whole HLD.
Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

Normalization adds **no additional full-body JSON serialization/deserialization pass** beyond the
one the OpenAI transform already performs. It operates within the existing parse→edit→serialize of
`OpenAiRequestTransformer`, not as a second independent parse of the request body.

## Verification

- Code review: normalization mutates the already-parsed request node graph inside the existing
  transform; there is no second `Parse` / `ToJsonString` round-trip introduced for normalization.
- Optional micro-benchmark on a representative large tool-bearing request to confirm added wall-time
  is within noise of the pre-feature transform.

## Acceptance Criteria

- The request is parsed once and serialized once on the forward path, as today.
- Added latency on the forward path is not measurably above the existing transform's cost.

## Applies To

Goal 1 (the seam) and Goal 4 (composability); the OpenAI transform path. Preserves the forward-path
latency profile assumed by HLD 001 LADR-003 (infinite-timeout streaming client).
