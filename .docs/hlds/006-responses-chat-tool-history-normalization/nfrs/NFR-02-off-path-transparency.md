# NFR-02: Off-path transparency

**Status:** Draft

## Requirement

There must be zero request-history mutation outside matched OpenAI imposter routes that downgrade inbound
`/responses` requests to Chat Completions. Passthrough/default OpenAI routes, OpenAI providers that support
`responses`, Anthropic routes, and direct Chat Completions callers remain byte-transparent for prior-turn
tool history.

## Verification

- L0 transformer tests assert that the cleanup is gated by the same route conditions as the existing
  `/responses`→Chat conversion.
- L2 integration tests compare an off-path request body with tool history and verify it is forwarded
  unchanged.
- Code review checks that no Host or Infrastructure path performs history cleanup.

## Acceptance Criteria

- A direct `/chat/completions` request with Chat-shaped tool history is not modified by this design.
- An OpenAI `responses` upstream route receives the original Responses history unchanged.
- Anthropic request bodies are unaffected.

## Applies To

Goal 3; all non-downgraded routing paths.
