# NFR-02: Payload fidelity

**Status:** Draft

## Requirement

Zero forwarded rewrites alter, drop or add a protected element — identifier, path, URL, number, version, code span, environment variable, negation, markdown structure, slash and dollar commands, `@` mentions, catalog tool and MCP names — measured over a fixture corpus covering each element class and redacted real Claude Code and Codex messages.

## Verification

L0 fixtures drive the deterministic check with a rewrite that breaks each protected class and assert it is rejected; a passing rewrite per class asserts it is accepted. A corpus run against a live optimizer model counts accepted rewrites that a second, independent check flags. Repeated on the NativeAOT executable.

Implemented as L0 tests: `MessageRewriteValidatorTests` holds a rejecting and an accepting fixture for every protected class (including the elements a rewrite adds, catalog and MCP tool names, and every harness tag), and `MessageRewriteFidelityCorpusTests` runs a corpus of Claude Code and Codex-shaped messages with clean and altering rewrites through the validator and a second, independent check built from whitespace tokens and character shape. The corpus and its review found two defects. A hyphenated dollar command (`$imposter-who`) could be swapped for another (`$imposter-newsession`) because only the part before the hyphen was compared; dollar commands now match hyphenated names like slash commands. And dropping one of several negations (`do not A and do not B` to `do not A and B`) was accepted because negations were compared by presence; each negation word must now occur exactly as often as before (a review found the first version still accepted an added occurrence). Other classes stay presence-based on purpose, since a rewrite may merge a repeated number or path without changing meaning, and a rewrite that merges two negations is discarded rather than risk an inverted instruction (LADR-04: the original wins on doubt). The corpus is deterministic: it measures the validator, not a live optimizer model, so the rejected-rewrite rate of a real model is still unmeasured (Open item 1). The native check ran on the host-platform NativeAOT executable only; `linux/amd64` and `linux/arm64` remain native-runner gates.

## Acceptance Criteria

- Every protected class — including slash and dollar commands, `@` mentions and catalog tool and MCP names — has a rejecting and an accepting fixture.
- The corpus run reports zero accepted rewrites with a changed protected element, and the rejected-rewrite rate.
- An optimizer answer that echoes harness or instruction text is rejected (validated under LADR-04 as an added element).

## Applies To

Goal 3; LADR-03, LADR-04.
