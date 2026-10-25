# LADR-12: Tool filter returns an edit, not a body

**Status:** Draft

## Context

- LADR-05 invokes the filter at the forward seam and returns a body string. LADR-07 already rewrites only `tools` and `tool_choice`, byte-preserving everything else.
- HLD 017 LADR-08 runs tool and message optimizations concurrently and merges edits into the original bytes. Two whole-body returns cannot merge without last-writer-wins.
- Behavior must not change: selection, thresholds, history protection and fail-open stay in HLD 014.

## Decision

1. **Return an edit to the filter's owned slice.** On a candidate pass the filter returns the pruned `tools` array and the cleaned `tool_choice`, or no change. It never returns a whole rewritten body.
2. **Byte preservation stays.** Every top-level value outside `tools` and `tool_choice` is forwarded from the original bytes. A pass removing nothing returns no change (same observable outcome as the current same-instance return).
3. **Merge rule belongs to the coordinator.** The HLD 017 coordinator applies enabled strategies' edits to the original bytes last. If two edits ever overlap, the coordinator forwards the original body. A failed, timed-out or cancelled branch contributes no edit.
4. **Skip and fail-open outcomes are no-change.** Disabled, path-excluded, empty catalog, deferred-loading, small-catalog, all-skip, circuit-open and fail-open passes return no change. Who-probe bypass is unchanged.

## Consequences

- Tool selection logic, ordering and logging are unchanged; only the return contract narrows.
- The coordinator can merge tool and message edits without re-parsing a rewritten body.
- Overlap is fail-safe: original bytes win, so no strategy can corrupt another's slice.

## Related

- **HLD 014 LADR-05** — seam unchanged, return narrows to edit.
- **HLD 014 LADR-07** — byte-preserving output restated as edit boundary.
- **HLD 017 LADR-08** — consumer of this contract.
