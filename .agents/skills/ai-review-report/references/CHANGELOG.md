# ai-review-report — Changelog

> Load only when updating the skill or auditing past decisions; not needed for routine execution.
>
> **Imported history:** entries before 2026-06-01 predate this polyrepo. Legacy names (`.ai/`, `gemini-code-review`, `manual-gemini-cli-code-review.yml`) do not exist here; preserved as audit trail.
>
> Full LADR narratives (Context/Decision/Consequences) live in the skill `AGENTS.md`.

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-07-03 | Added a `workflow_dispatch` manual entry path to `pipeline-ai-analyse.yml` (`pr_number` required, `max_incremental` optional cap override). Guard `if:` now also allows the dispatch event, derives the PR from the input, and the concurrency group keys on `inputs.pr_number` too. | LADR-042 |
| 2026-07-03 | Follow-up from PR #57 review: normalized `OPENCODE_REVIEW_REPORT_DISABLE_AGENTS_MD_CHECK` matching to be case-insensitive, documented that behavior, aligned the caller-template description with the main workflow, and added a focused validator disable test. | PR #57 review 4626304325 |
| 2026-07-03 | Added `OPENCODE_REVIEW_REPORT_DISABLE_AGENTS_MD_CHECK` and `disable_agents_md_check` workflow/caller inputs to globally bypass the full-review AGENTS.md / README.md / SKILL.md documentation validation when explicitly set. Default remains enabled. | NO-TICKET |
| 2026-06-30 | Added autonomous `ai-analyse` low/medium auto-fix loop: new `pipeline-ai-analyse.yml` `workflow_run` guard/analyse workflow, new `.agents/skills/ai-analyse` skill, edit-only `analyse` opencode agent, scoped review-section extractor, parameterized `opencode-with-fallback.sh`, package/plugin registration, docs, and loop cap variables `OPENCODE_ANALYSE_MODEL` / `OPENCODE_ANALYSE_MAX_INCREMENTAL`. | LADR-042 |
| 2026-06-30 | **Cross-org reusable-workflow fix.** Declared all seven `OPENCODE_*_API_KEY` secrets under `on.workflow_call.secrets` (`required: false`) so out-of-org callers can pass them by explicit mapping — GitHub honors `secrets: inherit` only same-org/enterprise, so a cross-org caller using `inherit` previously startup-failed ("workflow file issue"). Caller template `code-review-caller.yml` and README now map keys explicitly (works same-org **and** cross-org); `inherit` documented as the same-org-only shortcut. SKILL.md/AGENTS.md LADR-037 amended. | LADR-037 |
| 2026-06-29 | Disabled opencode session sharing: added `"share": "disabled"` to `opencode.json` (LADR-041). Prevents PR diff content from being auto-shared via opencode's share feature. `setup-opencode-config.sh` `is_ours` predicate updated to allow the `share` key at the top level so self-heal still triggers. | LADR-041 |
| 2026-06-26 | Added **Anthropic** as the seventh provider (`OPENCODE_REVIEW_REPORT_PROVIDER=ANTHROPIC` → provider-id `anthropic`, `@ai-sdk/anthropic`, key `OPENCODE_ANTHROPIC_API_KEY`). Fixed public base `https://api.anthropic.com` hardcoded in `opencode.json` (no URL Variable, not in `_inject_base_urls` — like OpenCode Go/OpenRouter). Three Claude models (`claude-opus-4-8`, `claude-sonnet-4-6`, `claude-haiku-4-5`). Wired through resolver (incl. `claude*` model-family fail-fast), setup-config `is_ours` set, gate `env:`/PROVIDER_ID map/bootstrap case + three `Anthropic Claude …` `model_preset` options, caller template, local-review harvest + help text, README Step 3 matrix + providers section + Secrets/Variables tables + env-var table, SKILL.md env-var table + model-family fail-fast + opencode-transport LADR ref, root `AGENTS.md` Non-Negotiables (two exceptions → three) + System Context provider list. LADR-040. | LADR-040 |
| 2026-06-14 | Added **OpenRouter** as the sixth provider (`OPENCODE_REVIEW_REPORT_PROVIDER=OPEN_ROUTER` → provider-id `openrouter`, `@openrouter/ai-sdk-provider`, key `OPENCODE_OPENROUTER_API_KEY`). Fixed public base `https://openrouter.ai/api/v1` hardcoded in `opencode.json` (no URL Variable, not in `_inject_base_urls` — like OpenCode Go). 13 vendor-prefixed models (DeepSeek/Qwen/GLM/MiniMax/MiMo/Hunyuan/Step/Nemotron, incl. the go-anthropic equivalents minimax/minimax-m2.7 and qwen/qwen3.6-max-preview; no Anthropic/OpenAI). Wired through resolver, setup-config `is_ours` set, gate `env:`/PROVIDER_ID map/bootstrap case + four `OpenRouter …` `model_preset` options, caller template, eval harness, local-review/eval harvest, aggregate display name, README, SKILL.md, both AGENTS.md. LADR-039; root `AGENTS.md` "sole exception" → two exceptions. | NO-TICKET |
| 2026-06-13 | Added skill `AGENTS.md` "Self-Review — Known Intentional Patterns (do NOT flag)" registry so self-review false positives (downstream `PR #<n>` provenance refs, `cmd_threads` EXIT trap, `MACHINE_READABLE_ACTION` `tail -1` parse, compressed one-line changelog rows) are durably skipped on any machine, not just via per-PR `/ai-review skip`; root `AGENTS.md` changelog pointer notes LADRs are not re-inlined. From PR #44 review 4491689018. | NO-TICKET |
| 2026-06-13 | Self-review remediation from PR #149 review: validator SHA resolution, provider-agnostic footer/parsing, portable chunk scripts, Copilot pagination, guidance drift fixes (switches, LADR-019, root-doc path). | NO-TICKET |
| 2026-06-11 | Platform-semantics hallucination guard: prompt edits for claim verification / `[SPECULATIVE]` downgrade; DR-015 rule + fixture; LADR-015 extended. | NO-TICKET |
| 2026-06-11 | Fence balancing for posted review bodies (`lib/balance-fences.sh`); GFM parity fix for nested code fences. | NO-TICKET |
| 2026-06-11 | Root `skills` symlink removed; Claude Code plugin loads via manifest `"skills"` field. | NO-TICKET |
| 2026-06-11 | npm/opencode plugin channel (LADR-038): `@generic-automation-and-it/smooth-ai-review` on GitHub Packages. | LADR-038 |
| 2026-06-11 | README install runbook updated for reusable-workflow + plugin channels. | — |
| 2026-06-10 | Hard chunk-prompt bounding + coverage-gaps-never-block + visible fail-closed banner (LADR-035/036); `test-chunk-prompt-budget.sh`. | LADR-035/036 |
| 2026-06-10 | Reusable-workflow channel via `$REVIEW_SKILL_DIR` indirection (LADR-037); caller template. | LADR-037 |
| 2026-06-10 | Claude Code plugin `smooth-ai-review` (all three skills). | — |
| 2026-06-09 | Per-provider gateway `baseURL` injected at install time (LADR-034). | LADR-034 |
| 2026-06-09 | `review` agent `bash` denied; `OPENCODE_GATEWAY_API_KEY` removed from `$GITHUB_ENV`; `actions/cache` SHA-pinned. | LADR-029 |
| 2026-06-09 | SKILL.md trimmed to runtime contract; editor context confirmed in AGENTS.md. | — |
| 2026-06-07 | Chunk-failure signal → out-of-band flag files (LADR-031). | LADR-031 |
| 2026-06-07 | Holistic aggregation runs for every PR incl. single-chunk (LADR-030, supersedes LADR-017). | LADR-030 |
| 2026-06-07 | Chunk review on locked-down `review` agent (LADR-029); empty-output fallback. | LADR-029 |
| 2026-06-07 | Max-file-count gate + `OPENCODE_*` → `OPENCODE_REVIEW_REPORT_*` env rename (LADR-032). | LADR-032 |
| 2026-06-07 | Opt-in LLM eval harness (LADR-033): precision vs DR-001…014 + recall; triage archive; post-merge canary. | LADR-033 |
| 2026-06-07 | `model_preset` dispatch dropdown + `minimax-m3` model. | — |
| 2026-06-07 | Eval harness env-var rename to `OPENCODE_REVIEW_REPORT_*`. | #6 |
| 2026-06-06 | Health check → opencode server `/global/health` (LADR-028); per-provider probes removed. | LADR-028 |
| 2026-06-06 | OpenCode Go as two providers split by SDK surface (LADR-027). | LADR-027 |
| 2026-06-06 | Provider key `litellm-gemini` → `gemini`; `baseURL` placeholders removed. | BNKI-001 |
| 2026-06-06 | PR #1 review fixes: fail-closed on empty output, fork handling, `is_ours` hardening. | BNKI-001 |
| 2026-06-06 | `/gemini-review` retired; `/ai-review` sole trigger. | BNKI-001 |
| 2026-06-06 | Env-selected provider GEMINI/COPILOT/OPENAI (LADR-026). | LADR-026 |
| 2026-06-01 | Allow `external_directory` reads (LADR-025); fixes failing chunks. | BNKI-001 |
| 2026-05-29 | Fix local deadlock on unreachable gateway; process-group `timeout` shim. | BNKI-001 |
| 2026-05-29 | Zero-setup local runs: credential harvest from shell rc files. | BNKI-001 |
| 2026-05-29 | Two-tier review chain + explicit orchestrator model (LADR-002/022). | BNKI-001 |
| 2026-05-29 | De-brand + env-var rename: skill dir, credential vars, model chain. | BNKI-001 |
| 2026-05-28 | Skill format migration to Agent Skills standard (`SKILL.md`, `assets/`, `references/`). | BNKI-001 |
| 2026-05-28 | Migration: `@google/gemini-cli` → `opencode` CLI (LADR-023). | LADR-023 |
| 2026-05-26 | Draft-PR behaviour documented as Key Behavior. | BNKI-001 |
| 2026-05-26 | LADR-022: non-analytical calls default to `auto` (later → explicit orchestrator). | BNKI-001 |
| 2026-05-25 | LADR-021: all-models-failed posts request-changes, exits green. | BNKI-001 |
| 2026-05-20 | Non-Negotiable: diff+LADR beats PR-body intent claims (DR-014). | BNKI-1190 |
| 2026-05-12 | LADR-017/018/019/020: aggregation optimisations (single-chunk short-circuit, Flash model, no read_file, skip sections ≤2 chunks). | BNKI-001 |
| 2026-04-24 | Fix Gemini CLI "trusted folders" overriding `--yolo`. | BNKI-001 |
| 2026-04-21 | Non-Negotiable: verify refactor suggestions not already applied. | BNKI-1066 |
| 2026-04-21 | Key Behavior: mode-aware regression analysis. | BNKI-1066 |
| 2026-04-16 | Removed gh install (pre-installed on self-hosted). | BNKI-001 |
| 2026-04-14 | Removed `pull_request_target: review_requested` (duplicate runs). | BNKI-001 |
| 2026-04-14 | Runner → `self-hosted-high-memory` (private-network proxy). | BNKI-001 |
| 2026-04-08 | Fix `---: command not found` in `aggregate-reviews.sh`. | BNKI-001 |
| 2026-04-07 | Release branch sync review mode (LADR-016). | BNKI-001 |
| 2026-04-07 | Model fallback chain → three tiers; removed non-existent models. | BNKI-001 |
| 2026-04-02 | Model fallback chain → five tiers (added Flash variants). | BNKI-001 |
| 2026-03-25 | Key Behavior: EF Core expression tree navigation (no NRE in `.Select()`). | BNKI-780 |
| 2026-03-24 | Clean Code principles added to review focus. | BNKI-001 |
| 2026-03-24 | RTK token optimization proxy (LADR-014; later superseded by LADR-023). | BNKI-001 |
| 2026-03-23 | LADR-015: strengthened Critical/High verification + diff integrity. | BNKI-970 |
| 2026-03-11 | Migration/schema chunk detection (LADR-013). | BNKI-001 |
| 2026-03-11 | Fix `read_file: command not found` (escaped backticks); Windows junction fallback. | BNKI-001 |
| 2026-03-11 | Removed `--open` flag; corrected output path. | BNKI-001 |
| 2026-03-10 | `local-review.sh` wrapper for local CLI execution. | BNKI-001 |
| 2026-03-10 | LADR-012: confidence tagging (`[VERIFIED]`/`[SPECULATIVE]`). | BNKI-001 |
| 2026-03-02 | Doc-only chunk detection; Skip Areas enforcement. | BNKI-001 |
| 2026-02-28 | AGENTS.md drift detection as HIGH priority. | BNKI-001 |
| 2026-02-28 | Remediation pass: template alignment. | BNKI-001 |
| 2026-02-28 | Template alignment: 2558→120 lines (~95% reduction). | BNKI-001 |
| 2026-02-21 | #115: Chunk review focus areas optimisation. | |
| 2026-02-17 | #113-#114: Adaptive chunk splitting (LADR-010), semantic grouping (LADR-011). | |
| 2026-02-06 | #111-#112: Fix Gemini miscounting "None found", fallback review action logic. | |
| 2026-01-16 | #108-#110: Rename step IDs, fix blocking review detection. | |
| 2026-01-05 | #104-#107: Blocking review detection, commit message triggers. | |
| 2026-01-02 | #103: Selective concurrency (LADR-009), supersede LADR-008. | |
| 2025-12-22 | #98-#101: Model display name, quota detection, template exclusion, minimize race. | |
| 2025-11-28 | #87-#97: Two-part aggregation, test pairing, file access, NFR context. | |
| 2025-10-28 | Initial chunked review implementation (LADR-001). | |
