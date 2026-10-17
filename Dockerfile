# syntax=docker/dockerfile:1
#
# Multi-stage build for the SmoothLlmImposter Host. The published GHCR image is
# built from this file; you can also build it locally with Docker or Podman. See
# .docs/wiki/setups/docker.run-smooth-llm-imposter.md for local build
# instructions.
#
# The build publishes NativeAOT (`-p:PublishAot=true`): a ~40 MB self-contained
# binary with no .NET runtime in the final image (runtime-deps + icu-libs only).
# AOT compiles per target platform — do NOT add `--platform=$BUILDPLATFORM` to
# the build stage: ILC cannot cross-compile, so multi-arch images rely on QEMU
# emulating the SDK stage per platform (same as before; see publish-image.yml).

# ── Build stage ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG TARGETARCH
WORKDIR /build

# NativeAOT on musl needs a C toolchain and zlib headers.
RUN apk add --no-cache clang build-base zlib-dev

# Central build/package management files first, then the source. Restoring the
# Host csproj transitively restores its referenced projects (Domain /
# Application / Infrastructure).
COPY SmoothLlmImposter.slnx Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/ src/

RUN --mount=type=cache,target=/root/.nuget/packages \
    case "$TARGETARCH" in \
      amd64) RID=linux-musl-x64 ;; \
      arm64) RID=linux-musl-arm64 ;; \
      *) echo "unsupported TARGETARCH: $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    dotnet restore src/SmoothLlmImposter.Host/SmoothLlmImposter.Host.csproj -r "$RID" && \
    dotnet publish src/SmoothLlmImposter.Host/SmoothLlmImposter.Host.csproj \
      -c Release -r "$RID" --self-contained -p:PublishAot=true -o /app --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────
# runtime-deps (not aspnet): the AOT binary is self-contained and carries
# Kestrel with it. icu-libs preserves the globalization behavior the app had
# on the aspnet base; without it the container would silently run invariant.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app ./

RUN apk add --no-cache icu-libs

# Bind 5080 to match the launch-profile and docs convention. The runtime-deps
# image ships the same non-root user as aspnet; run as it.
ENV ASPNETCORE_URLS=http://+:5080
EXPOSE 5080
USER $APP_UID

# No in-image HEALTHCHECK is defined (deferred — probe
# http://localhost:5080/health from the host, see
# .docs/wiki/setups/docker.run-smooth-llm-imposter.md).
ENTRYPOINT ["./SmoothLlmImposter.Host"]
