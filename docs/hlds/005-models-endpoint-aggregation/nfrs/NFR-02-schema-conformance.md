# NFR-02: Compatibility (OpenAI schema conformance)

**Status:** Accepted

<!-- One file per quality attribute. Horizontal concern spanning the whole HLD.
Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

The response is a valid OpenAI `ListModelsResponse`: a top-level object with `object == "list"` and
a `data` array, where each element has a non-empty string `id`, `object == "model"`, an integer
`created`, and a non-empty string `owned_by`. The set of `id` values equals the **distinct** set of
`to` strings across all OpenAI-dialect provider mappings — no duplicates, no missing target, no
extra entry.

## Verification

- L2 integration test deserializes the response with an OpenAI-shaped list-of-models model and
  asserts no deserialization error and all required fields present.
- L2 integration test runs against a multi-route config containing a duplicated `to` and asserts the
  duplicate collapses to one entry and the `id` set matches the configured distinct `to` set exactly.
- L0 unit test asserts an OpenAI catalogue with no mappings yields `object == "list"` with an empty
  `data` array (still schema-valid).

## Acceptance Criteria

- A standard OpenAI client (e.g. Codex) parses the response without error.
- `data[].id` is exactly the distinct OpenAI `to` set for the active config.
- Duplicates are removed; passthrough/default providers contribute no entries.

## Applies To

Goal 1 (aggregation + dedup) and Goal 2 (schema-valid response); the `GET /openai/v1/models`
flow ([LADR-01](../ladrs/LADR-01-advertise-to-names.md),
[LADR-04](../ladrs/LADR-04-synthetic-model-fields.md)).
