# NFR-01: Transparency preservation

**Status:** Accepted

## Requirement

For any request whose last user message is **not** the exact string `who?` (after
trimming), or for any request sent when `Imposter:WhoMessage:Enabled` is `false`, the
forward-path HTTP request to the upstream — method, URL, headers, and body — is
**byte-identical** to the request that would have been sent before this HLD existed.

## Verification

Integration tests (`Host.IntegrationTest`) cover the existing imposter, passthrough,
SSE, mid-stream disconnect, and auth-override scenarios with the feature enabled.
A dedicated "non-match transparency" test sends a realistic chat request with the
feature ON and asserts the stub upstream received a body equal to the transformed
body and a header set equal to the pre-HLD header set (no new headers, no removed
headers).

## Acceptance Criteria

- `dotnet test` passes the full L2 suite with the feature ON and with the feature OFF,
  with no test asserting a body or header change.
- The `messages` array is parsed on both the match and non-match paths when the
  feature is enabled, and the responder is skipped entirely when the feature is
  disabled. The parsed `JsonDocument` is disposed before the forwarder is invoked.
- No new header is added to the outbound request on the non-match path.

## Applies To

- Goal 5 (Transparency preserved).
- Every forward-path flow in the [C1 diagram](../diagrams/c4-context.md).
