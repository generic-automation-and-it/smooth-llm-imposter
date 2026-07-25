#!/usr/bin/env bash
# imposter-who.sh — send a non-streaming --who? probe to the SmoothLlmImposter router
# and print the synthetic "Imposter: <in> -> <out> (auth: ..., session: ...)" reply.
#
# Why this exists: the in-band --who? switch is bypassed on streaming requests
# (LADR-05), and most agent harnesses (Codex, Claude Code) stream by default.
# This script issues a NON-streaming request directly to the router, so the
# short-circuit fires and the harness's model rewrite (the "imposter") is
# reported without a single upstream token being spent.
#
# Usage:
#   imposter-who.sh                          # auto-detect dialect from $OPENAI_BASE_URL / $ANTHROPIC_BASE_URL
#   imposter-who.sh --dialect openai         # force OpenAI dialect (Chat Completions)
#   imposter-who.sh --dialect anthropic      # force Anthropic dialect (Messages)
#   imposter-who.sh --model gpt-5.5          # inbound model the router will see (default probe model: "who-probe")
#   imposter-who.sh --session caller-id     # attach a caller session id header/field so --newsession later has a target
#   imposter-who.sh --base-url http://...    # override the base URL (otherwise derived from *_BASE_URL env)
#   imposter-who.sh --port 5080            # override the port in the resolved base URL
#
# Exit codes: 0 = probe returned a synthetic reply; 1 = router did NOT short-circuit
# (likely streaming was forced, or the feature is disabled); 2 = env/curl failure.
set -euo pipefail

DIALECT=""
MODEL="who-probe"
SESSION=""
BASE_URL=""
PORT=""

print_usage() {
  sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'
  exit 0
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dialect) DIALECT="$2"; shift 2 ;;
    --model) MODEL="$2"; shift 2 ;;
    --session) SESSION="$2"; shift 2 ;;
    --base-url) BASE_URL="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    -h|--help) print_usage ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

# Resolve dialect: explicit flag wins; else derive from which *_BASE_URL is set,
# preferring OPENAI_BASE_URL when both are present. Fail with exit 2 if neither.
if [[ -z "$DIALECT" ]]; then
  if [[ -n "${OPENAI_BASE_URL:-}" ]]; then DIALECT="openai"
  elif [[ -n "${ANTHROPIC_BASE_URL:-}" ]]; then DIALECT="anthropic"
  else echo "neither OPENAI_BASE_URL nor ANTHROPIC_BASE_URL is set; pass --dialect or --base-url" >&2; exit 2
  fi
fi

if [[ -z "$BASE_URL" ]]; then
  case "$DIALECT" in
    openai) BASE_URL="${OPENAI_BASE_URL:?OPENAI_BASE_URL not set}" ;;
    anthropic) BASE_URL="${ANTHROPIC_BASE_URL:?ANTHROPIC_BASE_URL not set}" ;;
    *) echo "invalid --dialect: $DIALECT (expected openai|anthropic)" >&2; exit 2 ;;
  esac
fi

# Auth headers are optional: the router resolves credentials from its own config,
# not from the inbound request, so the probe needs no API key. If a key is set in
# the env (OPENAI_API_KEY / ANTHROPIC_API_KEY), forward it to avoid any auth gate
# the client might have layered in front of the router.
# Optional --port override: replace the port in whatever BASE_URL we resolved.
if [[ -n "$PORT" ]]; then
  if [[ ! "$PORT" =~ ^[0-9]+$ ]]; then
    echo "--port must be a positive integer: $PORT" >&2; exit 2
  fi
  # Match :<port> at the end of the URL (host:port, with or without a trailing path).
  # Replaces only the authority port, leaving host and path intact.
  if [[ "$BASE_URL" =~ ^(.+):[0-9]+(/.*)?$ ]]; then
    BASE_URL="${BASH_REMATCH[1]}:${PORT}${BASH_REMATCH[2]:-}"
  else
    echo "--port given but BASE_URL has no :<port> authority: $BASE_URL" >&2; exit 2
  fi
fi

AUTH_HEADERS=()
if [[ -n "${OPENAI_API_KEY:-}" && "$DIALECT" == "openai" ]]; then
  AUTH_HEADERS+=(-H "Authorization: Bearer $OPENAI_API_KEY")
fi
if [[ -n "${ANTHROPIC_API_KEY:-}" && "$DIALECT" == "anthropic" ]]; then
  AUTH_HEADERS+=(-H "x-api-key: $ANTHROPIC_API_KEY")
fi

SESSION_HEADERS=()
if [[ -n "$SESSION" ]]; then
  # The router resolves caller session identity from the `session_id` header first
  # (SessionIdentityResolver.HeaderCandidates), so send it as a header for both dialects.
  SESSION_HEADERS+=(-H "session_id: ${SESSION}")
fi

case "$DIALECT" in
  openai)
    ENDPOINT="${BASE_URL%/}/chat/completions"
    BODY=$(printf '{"model":"%s","messages":[{"role":"user","content":"--who?"}]}' "$MODEL")
    ;;
  anthropic)
    # ANTHROPIC_BASE_URL conventionally omits /v1; append it. Use max_tokens=1 to
    # keep the probe cheap if the router ever forwards (it won't on a match).
    ENDPOINT="${BASE_URL%/}/v1/messages"
    BODY=$(printf '{"model":"%s","max_tokens":1,"messages":[{"role":"user","content":"--who?"}]}' "$MODEL")
    ;;
  *) echo "invalid dialect: $DIALECT" >&2; exit 2 ;;
esac

RESPONSE=$(curl -sS --fail-with-body -w '\n%{http_code}' \
  -H 'content-type: application/json' \
  "${AUTH_HEADERS[@]}" \
  "${SESSION_HEADERS[@]}" \
  -d "$BODY" \
  "$ENDPOINT" 2>&1) || {
    echo "curl failed:" >&2
    echo "$RESPONSE" >&2
    exit 2
  }

HTTP_CODE=$(printf '%s' "$RESPONSE" | tail -n1)
BODY_OUT=$(printf '%s' "$RESPONSE" | sed '$d')

# A synthetic who-probe reply is always HTTP 200. Extract the friendly text.
if [[ "$HTTP_CODE" != "200" ]]; then
  echo "router returned HTTP $HTTP_CODE (expected 200 for a who-probe short-circuit):" >&2
  echo "$BODY_OUT" >&2
  exit 1
fi

case "$DIALECT" in
  openai)
    CONTENT=$(printf '%s' "$BODY_OUT" | python3 -c '
import json, sys
try:
    d = json.load(sys.stdin)
    print(d["choices"][0]["message"]["content"])
except Exception:
    print("", end="")
')
    ;;
  anthropic)
    CONTENT=$(printf '%s' "$BODY_OUT" | python3 -c '
import json, sys
try:
    d = json.load(sys.stdin)
    print("".join(b.get("text","") for b in d.get("content",[]) if b.get("type")=="text"))
except Exception:
    print("", end="")
')
    ;;
esac

if [[ -z "$CONTENT" ]]; then
  echo "router returned 200 but no synthetic content was found (feature may be disabled):" >&2
  echo "$BODY_OUT" >&2
  exit 1
fi

printf '%s\n' "$CONTENT"
