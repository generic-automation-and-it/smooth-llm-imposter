---
name: ai-review
switches:
  - "`analyse` - fetch the PR review and recommend fix/skip decisions; default when no `N=fix` or `N=skip` argument is present."
  - "`execute` - apply the requested fix/skip decisions and route results back to the right PR location."
  - "`N=fix` / `N=skip` - per-issue execute decisions, for example `1=fix 2=skip`; presence auto-selects execute mode."
  - "`--source=copilot` - force GitHub Copilot agent review parsing and thread reply/resolve behavior."
  - "`--source=other` - force non-Copilot review routing through PR description AI review notes."
description: Analyze and execute AI PR review feedback with fix/skip decisions. Use when a user asks to parse an AI review, apply selected fixes, and finalize review processing for GitHub or Azure DevOps pull requests. Detects the review source — for a GitHub Copilot agent review it replies to and resolves each linked review thread; otherwise it appends AI review notes to the PR description **and (MANDATORY) writes every skipped finding into the PR description's "Skip Areas / Known Issues" bullets** so the next review round does not re-raise them.
allowed-tools:
  - Bash(.agents/skills/ai-review/scripts/copilot-review.sh:*)
  - Bash(${CLAUDE_PLUGIN_ROOT}/.agents/skills/ai-review/scripts/copilot-review.sh:*)
  # Execute mode also runs deterministic GitHub/git plumbing outside the helper script:
  # fetch the review (`gh api`), route non-Copilot results to the PR description
  # (`gh pr edit`), and commit/push applied fixes. Mirrors the ai-analyse allowlist;
  # `/ai-review execute` is itself the user's explicit authorization for these.
  - Bash(gh api:*)
  - Bash(gh pr:*)
  - Bash(git add:*)
  - Bash(git commit:*)
  - Bash(git push:*)
models:
  claude: sonnet      # medium-complexity; review analysis + code fixes across multiple files
  copilot: auto
  codex: gpt-5.4
---

# AI PR Review Analyzer & Executor

Analyze AI PR review feedback and execute fix/skip decisions.

> **Script location.** Every `.agents/skills/ai-review/...` path in this document assumes the skill is installed in the repository (copy-install or this repo itself). When this skill runs from the **Claude Code plugin** (`smooth-ai-review`), the repository has no `.agents/skills/ai-review` tree — substitute `${CLAUDE_PLUGIN_ROOT}/.agents/skills/ai-review` for `.agents/skills/ai-review` in every script invocation (e.g. `"${CLAUDE_PLUGIN_ROOT}/.agents/skills/ai-review/scripts/copilot-review.sh" detect <pr>`).

## Invocation

The skill is invoked as `/ai-review <args>`. 

**Mode selection:**

1. **Explicit keyword** as the first argument: `analyse` or `execute`.
2. **Auto-detect** when no keyword is given:
   - If any argument matches `\d+=(fix|skip)` → **execute** mode.
   - Otherwise → **analyse** mode.

Examples:

```
/ai-review 48                              # auto → analyse
/ai-review analyse 48                      # explicit analyse
/ai-review 48 1=fix 2=skip                 # auto → execute
/ai-review execute 48 1=fix 2=skip         # explicit execute
```

**Review source selection (GitHub):**

The skill must determine whether the review being processed is a **GitHub Copilot agent review** or a review from another source (e.g. an OpenCode CLI agent, Gemini, or a generic AI reviewer). The source decides where execute results land — see [Execute → Result routing](#result-routing).

1. **Explicit switch**: `--source=copilot` or `--source=other`.
2. **Auto-detect** when no switch is given (the default): run the helper and use its verdict —
   ```bash
   .agents/skills/ai-review/scripts/copilot-review.sh detect <pr>   # prints COPILOT or OTHER
   ```
   It scans the PR's reviews and inline review comments for the Copilot reviewer bot (login `Copilot` / `copilot-pull-request-reviewer[bot]`). `COPILOT` → Copilot flow; `OTHER` → other.

> **GitHub plumbing lives in `scripts/copilot-review.sh`.** All deterministic GitHub calls (detect, fetch threads, reply, resolve, post summary) are subcommands of that script. The skill keeps the *judgment* — parsing the review, fix/skip decisions, and the text of every reply/summary. Reply and summary bodies are piped to the script via STDIN, so multi-line markdown is safe.

Examples:

```
/ai-review 48                                  # analyse, auto-detect source
/ai-review analyse 48 --source=copilot         # force Copilot-review parsing
/ai-review execute 48 1=fix 2=skip             # execute, source auto-detected
/ai-review execute 48 1=fix --source=other     # force non-Copilot result routing
```

## Two Modes: `analyse` and `execute`

### Mode 1: Analyse — Fetch review and recommend fixes/skips

**Use when**: User provides review URL, review ID, or just PR number

**Workflow:**

1. **Resolve PR number and review ID** from arguments
2. **Detect review source** (Copilot vs other) per [Review source selection](#invocation)
3. **Fetch review body** using `gh api` or `az repos pr` CLI
   - **Copilot flow (GitHub):** also pull the inline review comments and their threads so each parsed issue can be tied back to a specific Copilot comment:
     ```bash
     .agents/skills/ai-review/scripts/copilot-review.sh threads <pr>
     ```
     Returns JSON nodes of `{ id, isResolved, comments:[{ databaseId, path, author, body }] }`.
4. **Parse the review** to extract issues and suggested fixes. **Copilot flow:** record, per issue, the Copilot inline comment `databaseId` and its enclosing review thread `id` (from the `threads` output) so execute can reply and resolve the correct thread.
5. **Determine recommendation** for each issue:
   - Known intentional pattern: `skip`
   - AI hallucination: `skip`
   - Genuine bug or logic error: `fix`
   - Real simplification with no trade-offs: `fix`
   - Speculative / "consider" language: `skip`
   - Critical/High without exemption: `fix`

6. **Output analysis table** (the detected source is stated above the table):

| # | File | AI PR Review Recommendation | Priority | AI Coder Recommendation | AI Reviewer Reasoning |
|---|------|----------------------------|----------|------------------------|-----------------------|

   For the Copilot flow, retain the `commentId` / `threadId` mapping per row (used by execute) — it need not be printed in the table.

7. **Print summary** and suggested next command

8. **STOP** — Do NOT proceed to execute automatically. User decides whether and how to run execute.

---

### Mode 2: Execute — Apply fix/skip decisions

**Use when**: User provides decisions from analyse output

**Argument format**: `<pr-number> <1=fix|skip> <2=fix|skip> ...`

**Workflow:**

1. **Load review context** — Fetch latest AI review and **re-detect review source** (Copilot vs other) per [Review source selection](#invocation) so execute routes results correctly even when run as a standalone command
2. **Process each decision** — Apply fixes or prepare skip entries. **⛔ Non-Copilot flow — before leaving this step, every skipped finding must have a draft bullet ready for the "Skip Areas / Known Issues" section of the PR description (see [Result routing → Non-Copilot flow](#result-routing)). The bullets, not the summary table, are what the next review round reads. A skip without a bullet is a no-op. If the run is fix-only (zero `skip` decisions), skip the bullet-draft step entirely and proceed to step 3; do not fabricate an empty Skip Areas section.**
3. **Commit and push fixes** (only if any fixes were applied). Each fix gets its own commit (one commit per fix). **⚠️ When the review contains any 🔴 Critical or 🟠 High finding** (whether that specific finding is being fixed or skipped), **every fix commit message MUST include `/ai-review` as the last line of the commit body** — the workflow checks only the HEAD commit's message, so whichever fix commit ends up as HEAD when pushed must carry the trigger to force a full review immediately. Example commit message:
   ```
   fix(scope): address finding title

   <what changed and why>

   /ai-review
   ```
   For reviews with **only** medium/low findings, omit `/ai-review` from fix commit messages — the empty-commit step (5) is also skipped for medium/low-only reviews, so no full-review trigger is needed. Push all fix commits together in a single `git push`.
4. **Route results** — post the fix/skip summary table + analysis per [Result routing](#result-routing) below; for the Non-Copilot flow this step also **writes the skip bullets into the PR description** and verifies they landed
5. **Final empty commit** — **MANDATORY when any 🔴 Critical or 🟠 High priority issue appears in the review (fix OR skip) — no exceptions.** Commit message: `ci: /ai-review — processed review responses`. The fix commits from step 3 already carry `/ai-review` (when Critical/High findings exist), so the first push already triggered a full review. This empty commit is a **re-verification safety net** — it ensures the workflow sees a clean HEAD commit with the trigger after all routing edits (step 4) are complete, giving the re-verification run a stable diff to review. Do NOT skip this step, do NOT merge it into a fix commit, do NOT omit it because all high/critical items were skipped. For reviews with **only** medium/low findings, do NOT make this commit — the fix commits from step 3 suffice.
6. **Report completion** — only after the PR description verification in step 4 succeeded
7. **Review process improvements** (only if items were skipped)

<a id="result-routing"></a>
#### Result routing (where execute output lands)

The detected review source decides where the fix/skip summary table and analysis are posted.

**Copilot flow (`--source=copilot` or auto-detected Copilot review):**

1. **Per-thread reply + resolve (option A):** for **every** processed issue — both `fix` and `skip` — reply to the linked Copilot inline review comment with that row's decision and reasoning, then mark its review thread resolved:
   ```bash
   printf '%s' "**ai-review: FIX** — <what changed / commit ref>" \
     | .agents/skills/ai-review/scripts/copilot-review.sh reply <pr> <commentId>   # SKIP + reasoning for skipped rows
   .agents/skills/ai-review/scripts/copilot-review.sh resolve <threadId>
   ```
   - If a row has no mapped `commentId`/`threadId` (e.g. it came from the Copilot summary, not an inline comment), skip the per-thread step for that row and rely on the summary comment below.
2. **Summary comment:** post the full fix/skip summary table + analysis as one review-level comment on the PR so the overall outcome is visible in one place:
   ```bash
   cat summary.md | .agents/skills/ai-review/scripts/copilot-review.sh summary <pr>
   ```
3. **Do NOT** edit the PR description for the Copilot flow — results live on the review threads and the summary comment.

**Non-Copilot flow (`--source=other` or any non-Copilot review):**

> **⛔ MANDATORY — THIS IS THE LOAD-BEARING STEP, NOT THE TABLE.**
> The next review round (the chunked `ai-review-report` gate) reads the PR description's **"Skip Areas / Known Issues"** bullets to know which previously-raised findings are intentional and must not be re-flagged. **A fix/skip summary table appended on its own does NOT propagate skip decisions to the next review round** — the bullets do. If a Critical/High finding is marked `skip` but no corresponding bullet is added, the gate will raise the same finding again on the next run. This has been the failure mode in production. Treat the skip-bullets update as the primary action; the table is a secondary human-facing artifact.

Do **all** of the following, in order:

1. **Append the fix/skip summary table + responses block** to the PR description's **AI Review Notes** section (preserve existing content — append, never overwrite).
2. **MANDATORY — update the "Skip Areas / Known Issues" bullets in the PR description.** For **every** `skip` decision, add (or merge into) a bullet in that section so the next review round sees it:
   - Fetch the current PR description body: `gh pr view <pr> --json body -q .body`
   - Locate the section. Accept any of these headings (case-insensitive): `Skip Areas / Known Issues`, `Skip Areas`, `Known Issues`, `Known Skip Areas`, `Areas to Skip`. If none exists, **create** the section with heading `## Skip Areas / Known Issues` immediately above `## AI Review Notes` (or append at the end if that section is also missing).
   - For each skipped finding, add a bullet of the form: `- <file>:<line-or-range> — <one-line issue summary> — **skip reason:** <why it's intentional>`. If a bullet for the same file+line already exists with matching content, update it rather than duplicating.
   - Write the updated body back with `gh pr edit <pr> --body "$NEW_BODY"` (or pipe via `--body-file @-`). Preserve **all** other sections verbatim.
3. **Verify the edit landed** — immediately after `gh pr edit`, re-fetch the body, extract the Skip Areas section, and grep **that section** (not the full body — the appended fix/skip summary table contains the same `<file>:<line>` anchor and would mask a missing bullet):
   ```bash
   gh pr view <pr> --json body -q .body \
     | awk 'BEGIN{f=0; IGNORECASE=1} /^##[[:space:]]*(Skip Areas|Known Issues|Known Skip Areas|Areas to Skip)/{f=1; next} /^## /{f=0} f' \
     | grep -F "**skip reason:** <one-sentence reason from the bullet>"
   ```
   Use the `**skip reason:** …` tail (or one full bullet line quoted verbatim) as the grep anchor — that token is unique to the new bullet format and does **not** appear in the appended summary table. If the grep comes back empty, **this is a hard failure** — the skill has reproduced the known production bug. Re-fetch the body once more to rule out a transient GitHub read-path lag; if the second fetch also lacks the bullet, re-run step 2 and re-write. Do not proceed to step 6 ("Report completion") until the bullet's `**skip reason:**` substring is present in the Skip Areas section of the live PR description.

## Guardrails

- Never auto-execute after analyse mode
- **MANDATORY `/ai-review` trigger on fix commits AND empty commit:** when the review contains at least one 🔴 Critical or 🟠 High finding, **every fix commit** (step 3) must include `/ai-review` as the last body line so the first push triggers a full review immediately — the workflow checks only the HEAD commit's message, and with chunked commits any one could be HEAD. The final `ci: /ai-review — processed review responses` empty commit (step 5) is still mandatory as a re-verification safety net — never omit it, never fold it into a fix commit. Only omit both for medium/low-only reviews.
- Keep fixes scoped to selected items only
- **Copilot flow:** reply to and resolve only the threads for issues actually processed in this execute run; never resolve unrelated or human-authored threads
- **Non-Copilot flow:** preserve existing PR AI Review Notes content (append, never overwrite)
- **⛔ Non-Copilot flow — skip-bullets obligation:** appending the fix/skip summary table is **not sufficient**. Every skipped finding **must also** appear as a bullet in the PR description's **"Skip Areas / Known Issues"** section, and the skill **must verify** the bullets are present in the live PR body before reporting completion. The next review round reads those bullets, not the table; a skip without a bullet causes the same Critical/High finding to be re-raised on the next run. If any skipped item is missing from that section after the `gh pr edit`, the run is a failure — retry the edit rather than declaring success.
- Only suggest review-process improvements, don't apply them
