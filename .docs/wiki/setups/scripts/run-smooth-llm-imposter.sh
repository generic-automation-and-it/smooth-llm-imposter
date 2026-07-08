#!/usr/bin/env bash
# Example run script for SmoothLlmImposter via Podman.
#
# Assumes MYCOMPANY_ANTHROPIC_AUTH_TOKEN and MYCOMPANY_OPENAI_API_KEY are
# already exported in your shell (e.g. from ~/.zshrc or ~/.bashrc).
#
# Provider configuration is injected at run time via Imposter__ env vars so
# no local appsettings override file is needed.
#
# Usage:
#   chmod +x run-smooth-llm-imposter.sh
#   cp run-smooth-llm-imposter.sh ~/.local/bin/
#   run-smooth-llm-imposter.sh
set -euo pipefail

docker rm -f smooth-llm-imposter >/dev/null 2>&1 || true
docker run -d --name smooth-llm-imposter --restart unless-stopped \
  --platform linux/amd64 \
  -p 5080:5080 \
  -e MYCOMPANY_ANTHROPIC_AUTH_TOKEN \
  -e MYCOMPANY_OPENAI_API_KEY \
  -e "Imposter__Providers__mycompany-anthropic__Dialect=anthropic" \
  -e "Imposter__Providers__mycompany-anthropic__BaseUrl=https://models.example.mycompany.io/claude" \
  -e "Imposter__Providers__mycompany-anthropic__AuthScheme=Bearer" \
  -e "Imposter__Providers__mycompany-anthropic__Models__0__From=claude-opus-*" \
  -e "Imposter__Providers__mycompany-anthropic__Models__0__To=anthropic.{model}" \
  -e "Imposter__Providers__mycompany-anthropic__Models__1__From=claude-sonnet-*" \
  -e "Imposter__Providers__mycompany-anthropic__Models__1__To=anthropic.{model}" \
  -e "Imposter__Providers__mycompany-anthropic__Models__2__From=claude-haiku-*" \
  -e "Imposter__Providers__mycompany-anthropic__Models__2__To=anthropic.{model}-20251001-v1:0" \
  -e "Imposter__Providers__mycompany-openai__Dialect=openai" \
  -e "Imposter__Providers__mycompany-openai__BaseUrl=https://models.example.mycompany.io/openai" \
  -e "Imposter__Providers__mycompany-openai__AuthScheme=ApiKey" \
  -e "Imposter__Providers__mycompany-openai__AuthHeader=api-key" \
  -e "Imposter__Providers__mycompany-openai__Models__0__From=gpt-4o" \
  -e "Imposter__Providers__mycompany-openai__Models__0__To=gpt-4o-2024-08-06" \
  -e "Imposter__Providers__mycompany-openai__Models__1__From=gpt-5.4" \
  -e "Imposter__Providers__mycompany-openai__Models__1__To=gpt-5.4-2026-03-05" \
  -e "Imposter__Providers__mycompany-openai__Models__2__From=gpt-5.5" \
  -e "Imposter__Providers__mycompany-openai__Models__2__To=gpt-5.5-2026-04-24" \
  ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest
