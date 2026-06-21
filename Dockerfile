# syntax=docker/dockerfile:1
#
# Multi-stage build for the SmoothLlmImposter Host. There is no published
# registry image — build this locally with Docker or Podman. See
# .docs/wiki/setups/docker.run-smooth-llm-imposter.md for run instructions.

# ── Build stage ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build

# Central build/package management files first, then the source. Restoring the
# Host csproj transitively restores its referenced projects (Domain /
# Application / Infrastructure).
COPY SmoothLlmImposter.slnx Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/ src/

RUN dotnet restore src/SmoothLlmImposter.Host/SmoothLlmImposter.Host.csproj
RUN dotnet publish src/SmoothLlmImposter.Host/SmoothLlmImposter.Host.csproj \
      -c Release -o /app --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Bind 5080 to match the launch-profile and docs convention. The aspnet image
# ships a non-root user; run as it.
ENV ASPNETCORE_URLS=http://+:5080
EXPOSE 5080
USER $APP_UID

# Note: the aspnet runtime image has no curl/wget, so no in-image HEALTHCHECK —
# probe http://localhost:5080/health from the host instead (see the setup doc).
ENTRYPOINT ["./SmoothLlmImposter.Host"]
