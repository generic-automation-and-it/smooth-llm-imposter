#!/bin/bash
# Installs opencode.json from the skill source-of-truth
# (.agents/skills/ai-review-report/assets/opencode.json) into opencode's global
# config location (~/.config/opencode/opencode.json).
#
# Global scope (precedence 2 per opencode docs) is chosen over project scope
# so opencode finds the gemini provider regardless of which directory
# the review scripts are invoked from. The repo source remains the single
# committed source of truth.
#
# Install policy differs by environment to avoid clobbering a developer's
# personal opencode config (the dest is a shared, non-repo location):
#   - CI: always refresh. On an ephemeral GitHub-hosted runner the dest won't
#     exist yet; on a reused/self-hosted runner a stale config from a prior run
#     must be overwritten so the runner picks up the current provider definition.
#     Either way no human's personal config lives there, so overwriting is safe.
#   - Local, dest missing: install it (first run).
#   - Local, dest is OUR config (has a gemini block) but references the
#     OLD env-var names: self-heal — refresh it to the committed version so the
#     provider resolves OPENCODE_GEMINI_*. Already-current configs are left as-is.
#   - Local, dest is a hand-rolled personal config (no gemini block):
#     do NOT overwrite. Print actionable guidance so a later "provider/model
#     not found" failure is self-explanatory and the dev can merge it in.
#
# The committed opencode.json ships NO baseURL on the env-driven providers
# (gemini → @ai-sdk/google, github-copilot, openai), so each defaults to its
# native SDK endpoint. When a deployment fronts a provider with a gateway
# (e.g. a LiteLLM proxy), it sets OPENCODE_REVIEW_REPORT_<P>_URL and this script
# injects that value as the provider's options.baseURL in the INSTALLED config
# (_inject_base_urls, LADR-034). API keys are still read via {env:OPENCODE_*_API_KEY}.
# An empty/unset URL var → no baseURL added (native base kept), which is why the
# baseURL is injected dynamically rather than as a static {env:…} placeholder:
# an unset placeholder would substitute to an empty-string baseURL and break the
# SDK. The two OpenCode Go providers, OpenRouter, and the direct Anthropic
# provider are never injected — their base is a fixed public endpoint hardcoded
# in opencode.json (no URL var): https://opencode.ai/zen/go/v1,
# https://openrouter.ai/api/v1, and https://api.anthropic.com respectively.
#
# Must run AFTER actions/checkout — see LADR-023.

set -e

# Resolve repo root from this script's own location, not `git rev-parse`.
# local-review.sh sources this script BEFORE it cd's into the repo, so a
# `git rev-parse --show-toplevel` here crashes when local-review.sh is
# invoked by absolute path from outside a git working dir. SCRIPT_DIR is
# at .agents/skills/ai-review-report/scripts/lib → repo root is 5 levels up.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../../../.." && pwd)"
SRC="$REPO_ROOT/.agents/skills/ai-review-report/assets/opencode.json"
DEST_DIR="$HOME/.config/opencode"
DEST="$DEST_DIR/opencode.json"

if [ ! -f "$SRC" ]; then
  echo "❌ opencode.json source missing at $SRC" >&2
  exit 1
fi

mkdir -p "$DEST_DIR"

# Per-provider baseURL injection (LADR-034). For each env-driven provider whose
# OPENCODE_REVIEW_REPORT_<P>_URL is non-empty, set its options.baseURL in the
# installed config to that value (e.g. a LiteLLM proxy). Empty/unset → left
# alone (native SDK base). Idempotent: it always sets baseURL to the current env
# value, so a refreshed-from-SRC config (no baseURL) is re-injected each run.
# Only invoked for configs WE manage (see dest_managed below) — never for a
# hand-rolled personal config. Skipped (with a notice) when jq is unavailable.
_inject_base_urls() {
  local dest="$1" pair id var url tmp
  command -v jq >/dev/null 2>&1 || {
    echo "ℹ️  jq not found — skipping baseURL injection (providers use native SDK base)."
    return 0
  }
  # OpenCode Go, OpenRouter, and Anthropic are intentionally absent — their base
  # is a fixed public endpoint hardcoded in opencode.json, never injected
  # (LADR-027/039/LADR-040).
  for pair in "gemini:OPENCODE_REVIEW_REPORT_GEMINI_URL" \
              "github-copilot:OPENCODE_REVIEW_REPORT_COPILOT_URL" \
              "openai:OPENCODE_REVIEW_REPORT_OPENAI_URL"; do
    id="${pair%%:*}"; var="${pair#*:}"; url="${!var:-}"
    [ -n "$url" ] || continue
    jq -e --arg id "$id" '.provider[$id]' "$dest" >/dev/null 2>&1 || continue
    tmp="$(mktemp)"
    if jq --arg id "$id" --arg url "$url" \
          '.provider[$id].options.baseURL = $url' "$dest" > "$tmp" 2>/dev/null; then
      mv "$tmp" "$dest"
      echo "✓ baseURL injected for provider '$id' (from $var)."
    else
      rm -f "$tmp"
      echo "⚠️  Failed to inject baseURL for '$id' — left config unchanged." >&2
    fi
  done
}

# Tracks whether the final $DEST is a config we wrote/own (and may therefore
# inject into). Stays false for a left-untouched personal config.
dest_managed="false"

if [ "${CI:-}" = "true" ] || [ -n "${GITHUB_ACTIONS:-}" ]; then
  cp "$SRC" "$DEST"
  echo "✓ opencode.json installed (CI — always refreshed): $SRC → $DEST"
  dest_managed="true"
elif [ ! -f "$DEST" ]; then
  cp "$SRC" "$DEST"
  echo "✓ opencode.json installed: $SRC → $DEST"
  dest_managed="true"
elif grep -q '"gemini"' "$DEST" 2>/dev/null; then
  # The dest has our provider. Only auto-refresh if it is OUR managed shape —
  # solely the providers we ship plus our own optional `permission` and `agent`
  # blocks, no other top-level keys, AND every provider's apiKey is still our
  # {env:OPENCODE_*} placeholder. That apiKey clause is the real discriminator:
  # it distinguishes our config (which never holds a real key) from a personal
  # config that merely reuses the same provider keys but customizes options to
  # real keys — without it, a key match alone could clobber that personal config.
  # A stale-but-ours config (e.g. old {env:OPENCODE_LITELLM_*} names) still uses
  # the OPENCODE_ placeholder form, so self-heal/refresh is preserved.
  # baseURL is optional: an absent baseURL passes (committed shape — native SDK
  # base). A present baseURL may be (a) our {env:OPENCODE_*} form, (b) the
  # hardcoded https://opencode.ai/zen/go/v1 Zen base, or (c) any http(s):// URL
  # injected by _inject_base_urls (LADR-034) from OPENCODE_REVIEW_REPORT_<P>_URL —
  # all three keep this recognized as our managed config so self-heal still runs.
  # NOTE: jq's `keys` sorts alphabetically, so the committed provider set
  # compares as: anthropic, gemini, github-copilot, go-anthropic, go-openai,
  # openai, openrouter.
  is_ours="false"
  jq_available="true"
  if command -v jq >/dev/null 2>&1; then
    jq -e '
      ((keys - ["$schema","provider","permission","agent","share"]) == [])
      and ((.provider // {} | keys) == ["anthropic","gemini","github-copilot","go-anthropic","go-openai","openai","openrouter"])
      and ((.agent // {} | keys) | (. == [] or . == ["review"] or . == ["analyse","review"]))
      and (all((.provider // {})[]?;
            ((.options.apiKey // "") | test("^\\{env:OPENCODE_"))
            and ((.options.baseURL // "{env:OPENCODE_}") | test("^(\\{env:OPENCODE_|https?://)"))))
    ' "$DEST" >/dev/null 2>&1 && is_ours="true"
  else
    jq_available="false"
  fi
  if [ "$is_ours" = "true" ]; then
    if cmp -s "$SRC" "$DEST"; then
      echo "✓ opencode.json already current: $DEST"
    else
      # Our-shape but stale (old env-var names, or missing the new permission
      # block, or any other drift). Safe to refresh to the committed version.
      cp "$SRC" "$DEST"
      echo "♻️  Stale opencode.json detected — refreshed to the committed version (provider + permission): $DEST"
    fi
    dest_managed="true"
  elif [ "$jq_available" = "false" ]; then
    # jq is required to safely tell our managed config apart from a hand-rolled
    # personal one, so we can't auto-refresh — leave the file untouched (safe).
    echo "ℹ️  jq not found — can't verify $DEST is the managed config, so it's left untouched."
    echo "    Install jq (brew install jq / apt-get install jq) to enable auto-refresh of this config."
  else
    echo "⚠️  $DEST has a 'gemini' provider but also other settings —"
    echo "    NOT overwriting your personal config. Sync the provider blocks you use"
    echo "    (gemini → {env:OPENCODE_GEMINI_*}, github-copilot →"
    echo "    {env:OPENCODE_COPILOT_*}, openai → {env:OPENCODE_OPENAI_*},"
    echo "    anthropic → {env:OPENCODE_ANTHROPIC_API_KEY} with the hardcoded"
    echo "    https://api.anthropic.com baseURL; go-openai →"
    echo "    {env:OPENCODE_GO_OPENAI_API_KEY}, go-anthropic →"
    echo "    {env:OPENCODE_GO_ANTHROPIC_API_KEY} — both with the hardcoded"
    echo "    https://opencode.ai/zen/go/v1 baseURL; openrouter →"
    echo "    {env:OPENCODE_OPENROUTER_API_KEY} with the hardcoded"
    echo "    https://openrouter.ai/api/v1 baseURL) and the"
    echo "    top-level \"permission\": { \"external_directory\": \"allow\" } block from: $SRC"
  fi
else
  echo "⚠️  $DEST exists but has no 'gemini' provider — leaving your personal config untouched."
  echo "    If the review fails with a provider/model-not-found error, merge the"
  echo "    provider block for the provider you use (gemini / github-copilot"
  echo "    / openai / anthropic / go-openai / go-anthropic / openrouter) from:"
  echo "      $SRC"
  echo "    into your config at:"
  echo "      $DEST"
fi

# Inject per-provider gateway baseURLs (LADR-034) only into a config we manage —
# a left-untouched personal config keeps the user's own baseURLs.
if [ "$dest_managed" = "true" ]; then
  _inject_base_urls "$DEST"
fi
