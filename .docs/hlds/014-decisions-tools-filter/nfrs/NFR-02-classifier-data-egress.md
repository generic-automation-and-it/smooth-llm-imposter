# NFR-02: Deliberate classifier egress

**Status:** Accepted

## Requirement

Only configured model, bounded prompt state and bounded tool-label/description questions are sent to the decision service. Caller headers and provider credentials never accompany them. Classifier logs contain only path, counts, elapsed time, tool name/probability or failure reason type; never prompt text, descriptions, schemas or secrets.

The opt-in documentation identifies BaseUrl as a data-egress destination, including on passthrough. This requirement governs classifier diagnostics; existing operator-enabled raw routing dumps are separate behavior.

## Verification

Capture outbound decision requests and filter logs using sentinel prompt, schema, description and credential values.

## Acceptance Criteria

- Sentinels appear only in their permitted request fields, never classifier logs.
- No inbound auth or identity header reaches the decision service.
- Operator docs explain that a remote BaseUrl sends prompt/description data there.

## Applies To

Decision transport, filter logging and setup documentation.
