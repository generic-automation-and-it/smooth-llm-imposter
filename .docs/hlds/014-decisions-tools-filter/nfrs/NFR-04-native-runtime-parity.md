# NFR-04: Native decision-filter parity

**Status:** Accepted

## Requirement

Decision filtering and its startup options must function in the published native Host and Linux musl container, not just JIT tests. Bindable option leaves use the established source-generation path. Decision JSON uses JsonNode/JsonObject without new reflection-dependent serializer metadata or assembly scanning.

## Verification

Publish with PublishAot enabled, then exercise health and a filtered forward for Chat, Responses and Messages against live OpenJEV. Run host-native with the loopback BaseUrl override and container-native with the host.docker.internal default. Preserve native Linux AMD64/ARM64 release verification.

## Acceptance Criteria

- Native publish succeeds without disabling AOT or adding broad warning suppression.
- Native options bind non-default numeric/string overrides.
- All three APIs filter successfully in native host/container runs, including the large/concurrent cases in NFR-03.
- HLD remains Accepted until required runtime checks have evidence.

## Applies To

Option binding, decision JSON, explicit DI and the deployment artifact.
