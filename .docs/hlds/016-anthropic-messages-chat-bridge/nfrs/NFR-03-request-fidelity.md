# NFR-03: Mapped requests satisfy a strict Chat upstream

**Status:** Accepted

## Requirement

Every mapped request satisfies the structural invariants strict Chat upstreams enforce: each tool message immediately follows the assistant message holding its tool call, one per call id and in call order; tool names and ids match the allowed charset and length; no message is empty; only allowlisted fields are present. Tool-call arguments and tool inputs round-trip with their textual form (numbers, key order) unchanged across a tool turn. Output is byte-identical for identical input.

## Verification

A structural validator run over every request-spec matrix case and over a corpus of captured Claude Code request bodies (at least Bash, Read, Edit, Skill, an MCP tool, a subagent call, parallel tool calls and an image). A live evaluation sends the corpus to at least two distinct Chat upstreams.

## Acceptance Criteria

- Zero validator violations across matrix and corpus.
- Zero upstream 4xx caused by request structure in the live evaluation.
- Identical bytes on repeated mapping of the same body.

## Applies To

Request mapping and the tool name and id rules ([LADR-07](../ladrs/LADR-07-tool-identity.md)).
