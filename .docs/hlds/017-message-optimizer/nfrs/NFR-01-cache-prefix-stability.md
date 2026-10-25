# NFR-01: Cache-prefix stability

**Status:** Draft

## Requirement

While a session is retained in memory (LADR-05), every human message the stage has handled is forwarded with byte-identical text on every later request, whatever happened to it — rewritten, declined by the gate, discarded by validation, or skipped by an open circuit. A message may change only on its first handling: a new message, or history first seen after enablement or a restart (one accepted cache miss, Open item 3).

## Verification

An L2 replay of a multi-turn Claude Code–shaped conversation — tool-call continuations, a gate failure mid-way, an open optimizer circuit and a trial call — compares each forwarded body's messages with the previous forward of the same conversation, byte for byte. Repeated on the NativeAOT executable for `linux/amd64` and `linux/arm64`.

## Acceptance Criteria

- Every previously forwarded turn is a byte-identical prefix of the next forward.
- A circuit closing does not cause an earlier, already-handled message to be rewritten.

## Applies To

Goal 4; LADR-03, LADR-05, LADR-07.
