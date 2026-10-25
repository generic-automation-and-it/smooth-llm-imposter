# LADR-08: Reasoning becomes unsigned thinking; vendor profiles are explicit opt-ins

**Status:** Accepted

## Context

- Chat upstreams expose reasoning in vendor-specific fields; Messages thinking blocks carry a signature only Anthropic can issue.
- Some vendors reject or misbehave when reasoning from earlier tool turns is not sent back; most reject unknown fields.
- Sniffing the provider host to pick behavior is fragile and invisible to the operator.

## Decision

**Adopt** mapping upstream reasoning text to thinking blocks with an empty signature, dropping encrypted reasoning, and dropping thinking blocks from request history by default. Vendor-specific reasoning handling is a **named profile** selected by an explicit provider option (default none), never inferred from the host. The profile list and their rules are in the [request spec](../spec/01-request-mapping.md).

## Consequences

- Default behavior is safe for any compliant Chat upstream.
- Vendors that need reasoning echoed back work only after the operator selects their profile.
- Unsigned thinking is display-only; it is never accepted as proof of reasoning by a Messages upstream.
