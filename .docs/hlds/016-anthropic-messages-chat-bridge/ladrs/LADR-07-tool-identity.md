# LADR-07: Tool ids and names are normalized statelessly and reversed

**Status:** Draft

## Context

- Chat upstreams constrain tool names (charset, 64 characters) and some emit ids Messages clients and later Chat requests reject.
- MCP tool names routinely exceed 64 characters or contain characters Chat forbids.
- The router is stateless across requests.

## Decision

**Adopt** deterministic normalization: ids that are missing, illegal, too long or duplicated are replaced with synthesized ids; names are sanitized and, when too long, truncated with a hash suffix. The name map is rebuilt for every request from its tools and history, and applied in reverse to the response. A name the map does not know is passed through unchanged.

## Consequences

- The client always receives the tool name it defined, including long MCP names.
- No cross-request state or cache is needed; any replica can serve any turn.
- A model that invents a tool name reaches the client unchanged, where the client reports an unknown tool.
