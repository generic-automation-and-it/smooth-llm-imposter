---
description: 'Backend WireMock / upstream stubbing — how integration tests fake the upstream LLM provider'
globs: "tests/*.Host.IntegrationTest/**/*.cs"
paths:
  - "tests/*.Host.IntegrationTest/**/*.cs"
applyTo: 'tests/*.Host.IntegrationTest/**/*.cs'
alwaysApply: false
---
# Backend Upstream Stubbing Rules

## Scope

Applies to the L2 integration tests in `tests/SmoothLlmImposter.Host.IntegrationTest/`, which exercise the
real Host (routing, forwarder, body transforms) against a faked upstream LLM provider. This router is
stateless and key-less — there is **no database**; the only thing worth faking is the upstream HTTP call.

## Two stubbing strategies

1. **In-process stub transport (current default).** `ImposterAppFixture` boots the Host via
   `WebApplicationFactory` and replaces the named `imposter-upstream` `HttpClient`'s primary handler with
   `StubUpstreamHandler`, which captures the outbound request (URI, auth headers, transformed body) and
   returns a canned response. No network, no containers. Prefer this for asserting *what the forwarder
   sent upstream* — it is the cheapest and most precise.
2. **WireMock service (CI-provisioned).** The PR-gate job runs a `wiremock/wiremock` service container on
   `127.0.0.1:19091`. Use it only when a test genuinely needs a real HTTP endpoint (e.g. asserting
   streaming/SSE passthrough or transport-failure → 502 behaviour over the wire). Point a provider's
   `BaseUrl` at the WireMock host and program stubs through its admin API.

## Rules

1. Default to the in-process `StubUpstreamHandler` for request-capture assertions; reach for the WireMock
   service container only when real HTTP behaviour is under test.
2. Never assert on or forward `ApiKey` values beyond what a test explicitly configures — keys live only in
   `ImposterOptions` (config/env). Tests configure providers via in-memory config or `appsettings`.
3. If you add a shared WireMock stub helper, keep it as the single source of truth for stub response shapes
   so dialect-specific (OpenAI vs Anthropic) bodies stay consistent across tests.
