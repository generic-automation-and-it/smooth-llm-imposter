# NFR-03: Statelessness (no external dependency)

**Status:** Draft

<!-- One file per quality attribute. Horizontal concern spanning the whole HLD.
Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

Serving `GET /openai/v1/models` issues **zero** outbound upstream HTTP requests and opens **zero**
database connections. The response is produced from in-memory configuration alone, so the endpoint
answers correctly when no upstream is reachable, no provider `Secret` is set, and no
`ConnectionStrings:ImposterDb` is configured (the stateless/key-less default).

## Verification

- L2 integration test boots the Host with the stub upstream transport and asserts the stub records
  **no** request when `GET /openai/v1/models` is called.
- L2 integration test runs with no database connection string configured and asserts the endpoint
  returns `200` with the aggregated list (no credential-store access).

## Acceptance Criteria

- The upstream stub transport observes no call for this path.
- The endpoint returns the aggregated list with persistence unconfigured.
- No credential-store lookup occurs on this path (it does not enter the passthrough credential seam).

## Applies To

Goal 2; the replace-passthrough decision
([LADR-02](../ladrs/LADR-02-synthesize-locally.md)) and its narrow scope
([LADR-03](../ladrs/LADR-03-openai-get-only.md)).
