# LADR-06: History and dependent choice integrity

**Status:** Accepted

## Context

- Dropping a tool referenced by prior calls can make conversation replay invalid.
- Forced or allowed tool choices can become dangling after selection.
- Responses state pointers hide prior history from the router.

## Decision

**Protect** catalog entries referenced by supplied history and leave history unchanged. Chat custom calls receive the same protection as function calls; hosted type stems, MCP server labels, namespace names and namespace-child names protect their catalog units.

**Skip** Responses requests with non-null previous-response or conversation pointers, since the supplied delta cannot prove a tool is unused. Unknown tool shapes are never removed.

Prune understood object-form choices to surviving tools. Remove dangling forced choices and empty allowed-tools lists; when no tools remain, remove both tools and tool choice. Preserve unrecognized choice forms whenever tools remain.

Namespace-qualified named choices remain valid while their child definition survives inside the selected namespace.

## Consequences

- The optimization cannot intentionally invalidate known replay dependencies.
- Stateful Responses and unknown shapes receive less pruning.
- Tool-choice cleanup does not rename tools or rewrite historical calls.
