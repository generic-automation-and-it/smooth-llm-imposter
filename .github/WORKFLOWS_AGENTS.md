# GitHub Workflows Context

## Scope

Applies to workflow files under `.github/workflows/`.

## Container Publishing

- `publish-image.yml` publishes `ghcr.io/generic-automation-and-it/smooth-llm-imposter` from the repo-root `Dockerfile`.
- Keep the published image multi-architecture for local developer machines and servers: at minimum `linux/amd64` and `linux/arm64`.
- Keep QEMU configured before Buildx because the Dockerfile runs `dotnet restore` and `dotnet publish` during each
  target-platform build.
- The `sha-*`, `latest`, and semver tags should all point at the same multi-platform manifest list for a given workflow run.
- If the Dockerfile base images change, confirm the upstream .NET SDK/runtime images support every platform listed in the workflow.

## Change Log

| Date | Change | Issue |
|---|---|---|
| 2026-07-04 | Documented GHCR image publishing expectations, including required `linux/amd64` and `linux/arm64` platforms. | - |
