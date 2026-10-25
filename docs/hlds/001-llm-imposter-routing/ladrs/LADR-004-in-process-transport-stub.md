# LADR-004 — Integration tests stub the outbound transport in-process

- **Date / Status:** 2026-06-14 · Accepted

## Context

The template's integration harness required Postgres + Redis + WireMock containers via Aspire —
heavy and Docker-dependent for a stateless forwarder.

## Decision

Replace the `imposter-upstream` client's primary `HttpMessageHandler` with a capture stub,
exercising the real endpoint→router→transformer→forwarder pipeline with zero containers.

## Consequences

Tests run anywhere with no Docker/DB. WireMock/Aspire scaffolding was removed.
