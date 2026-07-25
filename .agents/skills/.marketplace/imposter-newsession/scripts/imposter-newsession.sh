#!/usr/bin/env bash
# imposter-newsession.sh — send a non-streaming --newsession probe to the
# SmoothLlmImposter router, minting a synthetic session id and storing a
# caller->synthetic mapping in the router's in-memory translation dictionary.
#
# Why this exists: like --who?, the --newsession switch is bypassed on streaming
# requests (LADR-05), and agent harnesses stream by default. This script issues a
# NON-streaming request directly to the router so the short-circuit fires and the
# mapping is minted. Subsequent forwards carrying the same caller session id are
# then translated to the synthetic id before reaching the upstream.
#
# Requires a resolvable caller session id: pass --session <id>. The router's
# SessionIdentityResolver reads it from:
#   - OpenAI: the body `session_id` field
#   - Anthropic: the `session_id` header
# When absent, --newsession is a no-match (forwards normally) and this script
# exits with code 1.
#
# Usage:
#   imposter-newsession.sh --session caller-id                 # auto-detect dialect
#   imposter-newsession.sh --session caller-id --model gpt-5.5 # force inbound model + OpenAI
#   imposter-newsession.sh --session caller-id --dialect anthropic --model claude-sonnet-4-6
#   imposter-newsession.sh --session caller-id --base-url http://...
#   imposter-newsession.sh --session caller-id --port 5080
set -euo pipefail

DIALECT=""
MODEL="newsession-probe"
SESSION=""
BASE_URL=""
PORT=""

print_usage() {
  sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
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

if [[ -z "$SESSION" ]]; then
  echo "--session caller-id is required: --newsession needs a resolvable caller session id to mint a synthetic one" >&2
  exit 2
fi

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

# Optional --port override: replace the port in whatever BASE_URL we resolved.
if [[ -n "$PORT" ]]; then
  if [[ ! "$PORT" =~ ^[1-9][0-9]*$ ]]; then
    echo "--port must be a positive integer (1-65535): $PORT" >&2; exit 2
  fi
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

# The router resolves caller session identity from the `session_id` header first
# (SessionIdentityResolver.HeaderCandidates), so send it as a header for both dialects.
AUTH_HEADERS+=(-H "session_id: ${SESSION}")

case "$DIALECT" in
  openai)
    ENDPOINT="${BASE_URL%/}/chat/completions"
    BODY=$(printf '{"model":"%s","messages":[{"role":"user","content":"--newsession"}]}' "$MODEL")
    ;;
  anthropic)
    ENDPOINT="${BASE_URL%/}/v1/messages"
    BODY=$(printf '{"model":"%s","max_tokens":1,"messages":[{"role":"user","content":"--newsession"}]}' "$MODEL")
    ;;
  *) echo "invalid dialect: $DIALECT" >&2; exit 2 ;;
esac

RESPONSE=$(curl -sS --fail-with-body -w '\n%{http_code}' \
  -H 'content-type: application/json' \
  "${AUTH_HEADERS[@]}" \
  -d "$BODY" \
  "$ENDPOINT" 2>&1) || {
    echo "curl failed:" >&2
    echo "$RESPONSE" >&2
    exit 2
  }

HTTP_CODE=$(printf '%s' "$RESPONSE" | tail -n1)
BODY_OUT=$(printf '%s' "$RESPONSE" | sed '$d')

if [[ "$HTTP_CODE" != "200" ]]; then
  echo "router returned HTTP $HTTP_CODE (expected 200 for a newsession short-circuit):" >&2
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

if [[ -z "$CONTENT" || "$CONTENT" != Session:* ]]; then
  echo "router returned 200 but no 'Session: <caller> -> <synthetic>' reply was found (feature may be disabled, or the caller session id was not resolved):" >&2
  echo "$BODY_OUT" >&2
  exit 1
fi

printf '%s\n' "$CONTENT"
