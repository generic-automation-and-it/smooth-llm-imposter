#!/bin/bash
# resolve-provider.sh — central provider selector for the OpenCode review pipeline.
#
# Single source of truth that maps the user-facing OPENCODE_REVIEW_REPORT_PROVIDER selector
# (GEMINI | COPILOT | OPENAI | ANTHROPIC | OPENCODE-GO-OPENAI | OPENCODE-GO-ANTHROPIC | OPEN_ROUTER,
# default GEMINI) onto:
#   - OPENCODE_REVIEW_REPORT_PROVIDER_ID         the provider KEY in assets/opencode.json that
#                                  opencode-with-fallback.sh prefixes onto the
#                                  model (`opencode run --model <id>/<model>`):
#                                    GEMINI                → gemini
#                                    COPILOT               → github-copilot
#                                    OPENAI                → openai
#                                    ANTHROPIC             → anthropic
#                                    OPENCODE-GO-OPENAI    → go-openai
#                                    OPENCODE-GO-ANTHROPIC → go-anthropic
#                                    OPEN_ROUTER           → openrouter
#                                  OpenCode Go is split into two providers because
#                                  it serves two SDK surfaces under one Zen gateway:
#                                  OpenAI-compatible (deepseek/kimi) and
#                                  Anthropic-compatible (qwen/minimax). A single
#                                  opencode.json provider block can pin only one npm.
#   - OPENCODE_REVIEW_REPORT_GATEWAY_URL /       the selected provider's gateway credentials,
#     OPENCODE_GATEWAY_API_KEY     copied out of the provider-specific
#                                  OPENCODE_REVIEW_REPORT_<P>_URL / OPENCODE_<P>_API_KEY pair
#                                  (EXCEPT the two OpenCode Go providers, whose
#                                  URL is the fixed literal https://opencode.ai/
#                                  zen/go/v1 — hardcoded in opencode.json too — so
#                                  only their API key comes from an env var).
#                                  These generic names are what the credential
#                                  presence checks read, so that check is not
#                                  Gemini-specific. (Health is checked separately
#                                  and provider-agnostically via the opencode
#                                  server — lib/opencode-health.sh — not here.)
#                                  The provider-specific names stay exported too —
#                                  opencode.json's {env:OPENCODE_<P>_*}
#                                  substitution references those literal names.
#
# Fail-fast (per LADR — provider switch): a misconfigured run dies here with an
# actionable message rather than limping on to a confusing empty/auth-error review:
#   - the selected provider's URL + API key must be non-empty;
#   - for any provider OTHER than GEMINI, the resolved review-model chain
#     (OPENCODE_REVIEW_REPORT_MODEL_PRIMARY / _SECONDARY / _ORCHESTRATOR) must be
#     set and must NOT name a `gemini*` model — those IDs don't resolve on the
#     Copilot/OpenAI gateways (their declared models are gpt-5.5 / gpt-5.4 /
#     gpt-5.4-mini in opencode.json).
#
# Dual-mode: exports the resolved vars into the current shell (so it can be
# `source`d by local-review.sh) and, when running as a CI step ($GITHUB_ENV set),
# also appends them to $GITHUB_ENV so later steps inherit them. `exit 1` on
# failure terminates the parent in both modes (intended — abort the run).

_rp_die() { echo "❌ $*" >&2; exit 1; }

OPENCODE_REVIEW_REPORT_PROVIDER="${OPENCODE_REVIEW_REPORT_PROVIDER:-GEMINI}"
OPENCODE_REVIEW_REPORT_PROVIDER="$(printf '%s' "$OPENCODE_REVIEW_REPORT_PROVIDER" | tr '[:lower:]' '[:upper:]')"

# OpenCode Go's gateway is a fixed public endpoint (the OpenCode Zen base
# https://opencode.ai/zen/go/v1, hardcoded in opencode.json too), so its
# providers carry no URL env var — _rp_url_fixed supplies the value the health
# probe needs. OpenRouter is the same shape: a single public aggregator
# (https://openrouter.ai/api/v1, hardcoded in opencode.json) with no
# per-deployment URL to retune (LADR-039). Anthropic is likewise a single
# public API (https://api.anthropic.com, hardcoded in opencode.json) with no
# per-deployment URL to retune. The other providers read their gateway URL
# from an env var.
_rp_url_fixed=""
case "$OPENCODE_REVIEW_REPORT_PROVIDER" in
  GEMINI)                _rp_id="gemini";         _rp_url_var="OPENCODE_REVIEW_REPORT_GEMINI_URL";        _rp_key_var="OPENCODE_GEMINI_API_KEY" ;;
  COPILOT)               _rp_id="github-copilot"; _rp_url_var="OPENCODE_REVIEW_REPORT_COPILOT_URL";        _rp_key_var="OPENCODE_COPILOT_API_KEY" ;;
  OPENAI)                _rp_id="openai";         _rp_url_var="OPENCODE_REVIEW_REPORT_OPENAI_URL";         _rp_key_var="OPENCODE_OPENAI_API_KEY" ;;
  ANTHROPIC)             _rp_id="anthropic";      _rp_url_var="";  _rp_url_fixed="https://api.anthropic.com"; _rp_key_var="OPENCODE_ANTHROPIC_API_KEY" ;;
  OPENCODE-GO-OPENAI)    _rp_id="go-openai";      _rp_url_var="";  _rp_url_fixed="https://opencode.ai/zen/go/v1"; _rp_key_var="OPENCODE_GO_OPENAI_API_KEY" ;;
  OPENCODE-GO-ANTHROPIC) _rp_id="go-anthropic";   _rp_url_var="";  _rp_url_fixed="https://opencode.ai/zen/go/v1"; _rp_key_var="OPENCODE_GO_ANTHROPIC_API_KEY" ;;
  OPEN_ROUTER)           _rp_id="openrouter";     _rp_url_var="";  _rp_url_fixed="https://openrouter.ai/api/v1";   _rp_key_var="OPENCODE_OPENROUTER_API_KEY" ;;
  *) _rp_die "Unknown OPENCODE_REVIEW_REPORT_PROVIDER='$OPENCODE_REVIEW_REPORT_PROVIDER' (expected GEMINI, COPILOT, OPENAI, ANTHROPIC, OPENCODE-GO-OPENAI, OPENCODE-GO-ANTHROPIC, or OPEN_ROUTER)." ;;
esac

OPENCODE_REVIEW_REPORT_PROVIDER_ID="$_rp_id"
if [ -n "$_rp_url_var" ]; then
  OPENCODE_REVIEW_REPORT_GATEWAY_URL="${!_rp_url_var}"
else
  OPENCODE_REVIEW_REPORT_GATEWAY_URL="$_rp_url_fixed"
fi
OPENCODE_GATEWAY_API_KEY="${!_rp_key_var}"

# Selected provider's credentials must be present. (For OpenCode Go and
# OpenRouter the URL is a fixed public base so this check is a no-op for them;
# the error message fallback "its gateway URL" is unreachable for those.)
[ -n "$OPENCODE_REVIEW_REPORT_GATEWAY_URL" ]     || _rp_die "OPENCODE_REVIEW_REPORT_PROVIDER=$OPENCODE_REVIEW_REPORT_PROVIDER selected but ${_rp_url_var:-its gateway URL} is empty/unset. Set it (GitHub Variable / shell export)."
[ -n "$OPENCODE_GATEWAY_API_KEY" ] || _rp_die "OPENCODE_REVIEW_REPORT_PROVIDER=$OPENCODE_REVIEW_REPORT_PROVIDER selected but $_rp_key_var is empty/unset. Set it (GitHub Secret / shell export)."

# Every provider requires an explicit model chain. We don't enumerate every valid
# id — non-Gemini gateways serve many families (gpt-*, o*, claude-*, …) and an
# allow-list would block future ones. Instead fail fast HERE on the mismatch
# guaranteed to break: a GEMINI gateway needs gemini-* ids, ANTHROPIC needs
# claude-* ids, and any other gateway must NOT carry a leftover gemini-* or
# claude-* id (it won't resolve on Copilot/OpenAI/Go-OpenAI/Go-Anthropic/OpenRouter).
# OPENCODE_ANALYSE_MODEL is the autonomous-fix model (pipeline-ai-analyse.yml) and is
# subject to the same family fail-fast — otherwise a mismatched analyse model (e.g. the
# Gemini default left in place under an ANTHROPIC provider) would silently degrade
# through the fallback chain to a different model. Unlike the three review tiers it is
# OPTIONAL: the review gate never sets it, so an empty value is skipped, not fatal.
for _rp_mv in OPENCODE_REVIEW_REPORT_MODEL_PRIMARY OPENCODE_REVIEW_REPORT_MODEL_SECONDARY OPENCODE_REVIEW_REPORT_MODEL_ORCHESTRATOR OPENCODE_ANALYSE_MODEL; do
  _rp_val="${!_rp_mv}"
  if [ -z "$_rp_val" ]; then
    case "$_rp_mv" in
      OPENCODE_ANALYSE_MODEL) continue ;;  # optional: analyse pipeline only, gate leaves it unset
      *) _rp_die "OPENCODE_REVIEW_REPORT_PROVIDER=$OPENCODE_REVIEW_REPORT_PROVIDER selected but $_rp_mv is unset. Set the OPENCODE_REVIEW_REPORT_MODEL_* Variables to this provider's models." ;;
    esac
  fi
  _rp_lc="$(printf '%s' "$_rp_val" | tr '[:upper:]' '[:lower:]')"
  case "$OPENCODE_REVIEW_REPORT_PROVIDER" in
    GEMINI)
      case "$_rp_lc" in
        gemini*) ;;
        *) _rp_die "OPENCODE_REVIEW_REPORT_PROVIDER=GEMINI selected but $_rp_mv='$_rp_val' is not a Gemini model (expected an id starting with 'gemini'). It won't resolve on the Gemini gateway." ;;
      esac
      ;;
    ANTHROPIC)
      case "$_rp_lc" in
        claude*) ;;
        *) _rp_die "OPENCODE_REVIEW_REPORT_PROVIDER=ANTHROPIC selected but $_rp_mv='$_rp_val' is not a Claude model (expected an id starting with 'claude'). It won't resolve on the Anthropic gateway." ;;
      esac
      ;;
    *)
      case "$_rp_lc" in
        gemini*) _rp_die "OPENCODE_REVIEW_REPORT_PROVIDER=$OPENCODE_REVIEW_REPORT_PROVIDER selected but $_rp_mv='$_rp_val' is a Gemini model. It won't resolve on the $OPENCODE_REVIEW_REPORT_PROVIDER gateway — set the OPENCODE_REVIEW_REPORT_MODEL_* Variables to this provider's models." ;;
        claude*) _rp_die "OPENCODE_REVIEW_REPORT_PROVIDER=$OPENCODE_REVIEW_REPORT_PROVIDER selected but $_rp_mv='$_rp_val' is a Claude model. It won't resolve on the $OPENCODE_REVIEW_REPORT_PROVIDER gateway — set the OPENCODE_REVIEW_REPORT_MODEL_* Variables to this provider's models." ;;
        *) ;;
      esac
      ;;
  esac
done

# Health checking is no longer per-provider. The single health signal is opencode
# itself (lib/opencode-health.sh: `opencode serve` + /global/health), which is
# identical for every provider — so there is no gateway health URL or per-surface
# auth style to derive here. This resolver only maps the provider → id + creds and
# fails fast on a bad model chain; the credential presence checks above guard the
# key. (Removed: OPENCODE_GATEWAY_HEALTH_URL, OPENCODE_GATEWAY_AUTH_STYLE,
# OPENCODE_API_HEALTH_OVERRIDE.)

export OPENCODE_REVIEW_REPORT_PROVIDER OPENCODE_REVIEW_REPORT_PROVIDER_ID OPENCODE_REVIEW_REPORT_GATEWAY_URL OPENCODE_GATEWAY_API_KEY

if [ -n "${GITHUB_ENV:-}" ]; then
  # Only the non-sensitive resolved vars go to $GITHUB_ENV. The API key is NOT
  # written here: it would persist the secret to every subsequent workflow step,
  # and nothing downstream reads OPENCODE_GATEWAY_API_KEY (opencode.json
  # substitutes the provider-specific {env:OPENCODE_*_API_KEY}; the presence check
  # above uses the in-process value). It stays exported in-process for local runs.
  {
    echo "OPENCODE_REVIEW_REPORT_PROVIDER=$OPENCODE_REVIEW_REPORT_PROVIDER"
    echo "OPENCODE_REVIEW_REPORT_PROVIDER_ID=$OPENCODE_REVIEW_REPORT_PROVIDER_ID"
    echo "OPENCODE_REVIEW_REPORT_GATEWAY_URL=$OPENCODE_REVIEW_REPORT_GATEWAY_URL"
  } >> "$GITHUB_ENV"
fi

echo "🔀 OpenCode provider: $OPENCODE_REVIEW_REPORT_PROVIDER (provider-id: $OPENCODE_REVIEW_REPORT_PROVIDER_ID)"

unset _rp_id _rp_url_var _rp_url_fixed _rp_key_var _rp_mv _rp_val _rp_lc
