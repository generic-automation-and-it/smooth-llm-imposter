# NFR-01: Streaming integrity (request-only normalization)

**Status:** Draft — **scoped by LADR-05 / NFR-05**

<!-- One file per quality attribute. Horizontal concern spanning the whole HLD.
Status lifecycle: Draft → Prototype → Accepted. -->

> **Scoped by LADR-05.** Byte-for-byte response relay is the rule for **tool normalization** and for
> every transparent/passthrough route. The one exception is the `/responses`→Chat *downgrade* path,
> where the response stream is translated to Responses events under NFR-05 (incremental, never
> buffered). This NFR governs all other paths unchanged.

## Requirement

Tool normalization touches the **request only**. On every path except the LADR-05 downgrade bridge,
the upstream response is relayed byte-for-byte: zero response-body reads, parses, buffers, or rewrites
are introduced, and no per-request state is consumed on the response path.

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
