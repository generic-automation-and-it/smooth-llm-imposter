# LADR-01: Internal evidence contract, consumer-owned policy

**Status:** Accepted

## Context

- Tool filtering already separates choice interpretation from HTTP execution, but the client is configured through routing options.
- A generic transport can be reused without creating a generic business-policy engine.
- The approved scope is internal reuse, not an endpoint for ai-review or other external callers.

## Decision

**Keep** the shared contract consumer-neutral and internal to SmoothLlmImposter. Consumers supply model selection, state and independently keyed choice questions; the integration returns the service envelope without domain-specific rewriting. A shared default model may supply model selection when the consumer does not override it.

Consumers own eligibility, prompt composition, question criteria, batching, thresholds, missing-answer interpretation and actions. The integration validates the successful response envelope, not the correctness of a consumer's decision. A model answer is evidence under the consumer's policy, not authority to route requests, remove tools or execute a workflow.

Application owns the contract; Infrastructure owns transport. A workflow DSL, decision-tree traversal engine, provider registry and public decision API are excluded: none is needed to reuse the existing boundary.

## Consequences

- HLD 014 remains the sole owner of tool-selection policy.
- Non-tool contract fixtures can demonstrate reuse without adding a production feature.
- Consumers must define their own behavior under uncertainty; the shared layer cannot provide a universal fail-open action.

## Open

- Revisit richer question types only when a real consumer requires a service contract beyond choice questions.
