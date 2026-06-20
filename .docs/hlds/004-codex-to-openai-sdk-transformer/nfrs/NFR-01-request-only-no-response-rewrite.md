# NFR-01: Streaming integrity (request-only normalization)

**Status:** Draft

<!-- One file per quality attribute. Horizontal concern spanning the whole HLD.
Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

Normalization touches the **request only**. The upstream response is relayed byte-for-byte: zero
response-body reads, parses, buffers, or rewrites are introduced by normalization, and no
per-request state created by normalization is consumed on the response path.

## Verification

- Integration test (L2, in-process stub upstream): a normalized imposter request whose stubbed
  upstream returns a known SSE stream yields a response **byte-identical** to the stub's bytes.
- Code review / static check: the normalization seam has no reference to the response message or the
  `RoutingEndpoints` streaming copy; the response path is unchanged from pre-feature.

## Acceptance Criteria

- The streamed bytes the client receives equal the bytes the upstream produced (no transformation).
- No normalization code reads or holds the response, and no request-side map is keyed for response
  lookup.

## Applies To

Goal 2 (request-only); the normalization seam in the Application layer and the Host streaming copy.
Upholds HLD 001 LADR-003.
