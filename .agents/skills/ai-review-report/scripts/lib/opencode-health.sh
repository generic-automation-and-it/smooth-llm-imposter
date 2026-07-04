#!/bin/bash
# opencode-health.sh — provider-agnostic health check via the opencode server.
#
# Replaces the old per-provider gateway probes (each provider's /models,
# /v1beta/models, or /health endpoint, each with its own auth header). The
# single health signal now is opencode ITSELF, identically for every
# OPENCODE_REVIEW_REPORT_PROVIDER: start `opencode serve` on localhost, read the URL it prints
#   opencode server listening on http(s)://127.0.0.1:4096
# hit that server's /global/health, then tear the server down. No per-provider
# URL/auth derivation, so it works the same for gemini / copilot / openai /
# go-openai / go-anthropic / openrouter.
#
# NOTE: /global/health reports that opencode itself is up — it does NOT prove the
# upstream model gateway is reachable or the API key valid. The real functional
# check remains the "Assert Review Model Selection Works" step (CI) / the chunk
# run (local), which makes an actual model call.
#
# Requires opencode on PATH AND its provider config already installed (so
# `serve` starts cleanly), plus curl.
#
# Optional env:
#   OPENCODE_REVIEW_REPORT_HEALTH_TIMEOUT  seconds to wait for the server + health (default 30)
#
# Exit 0 if /global/health returns 200; 1 otherwise. Callers decide whether a
# failure is fatal (local preflight) or a non-blocking warning (CI diagnostic).

set -u

TIMEOUT="${OPENCODE_REVIEW_REPORT_HEALTH_TIMEOUT:-30}"
LOG="/tmp/opencode-serve.$$.log"

if ! command -v opencode >/dev/null 2>&1; then
  echo "❌ opencode CLI not found on PATH — install it before the health check." >&2
  exit 1
fi

# Disable Claude Code (.claude) support to avoid conflicts with opencode.
# OPENCODE_REVIEW_REPORT_DISABLE_CLAUDE_CODE can override the value (default 1).
export OPENCODE_DISABLE_CLAUDE_CODE="${OPENCODE_REVIEW_REPORT_DISABLE_CLAUDE_CODE:-1}"

# Start the server in the background; capture its stdout/stderr so we can parse
# the listening URL (opencode picks the port, so we don't assume 4096).
opencode serve >"$LOG" 2>&1 &
_serve_pid=$!
trap '[ -n "${_serve_pid:-}" ] && kill "$_serve_pid" 2>/dev/null; wait "$_serve_pid" 2>/dev/null || true; rm -f "$LOG" "/tmp/opencode-health.$$.out" 2>/dev/null || true' EXIT

# Single deadline shared by both waits below, so TIMEOUT bounds the TOTAL
# (server-up + health-ready) wait — not 2× TIMEOUT as two independent loops did.
DEADLINE=$((SECONDS + TIMEOUT))

# Wait for the "listening on <url>" line (or the process to die early).
BASE=""
while [ "$SECONDS" -lt "$DEADLINE" ]; do
  BASE="$(grep -oE 'https?://[^[:space:]]+' "$LOG" 2>/dev/null | head -1)"
  [ -n "$BASE" ] && break
  kill -0 "$_serve_pid" 2>/dev/null || { echo "❌ opencode serve exited before reporting a URL:" >&2; tail -n 20 "$LOG" >&2 2>/dev/null || true; exit 1; }
  sleep 1
done

if [ -z "$BASE" ]; then
  echo "❌ opencode serve did not report a listening URL within ${TIMEOUT}s." >&2
  tail -n 20 "$LOG" >&2 2>/dev/null || true
  exit 1
fi

HEALTH_URL="${BASE%/}/global/health"
echo "Probing opencode health: ${HEALTH_URL}"

# Poll /global/health until 200 (routes come up a beat after the socket binds).
# Shares DEADLINE with the URL wait above (see note there).
http_code="000"
while [ "$SECONDS" -lt "$DEADLINE" ]; do
  http_code="$(curl -sS -o "/tmp/opencode-health.$$.out" -w '%{http_code}' --max-time 5 "$HEALTH_URL" 2>/dev/null || echo 000)"
  [ "$http_code" = "200" ] && break
  sleep 1
done

if [ "$http_code" = "200" ]; then
  echo "✓ opencode server healthy (${HEALTH_URL})"
  exit 0
fi

echo "⚠️ opencode /global/health did not return 200 (last: ${http_code}) at ${HEALTH_URL}" >&2
tail -n 20 "$LOG" >&2 2>/dev/null || true
exit 1
