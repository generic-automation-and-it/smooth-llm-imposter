# ai-brain-dump — Intent & Token-Usage Review

> Companion notes to [`SKILL.md`](./SKILL.md). Explains what this skill is for and why
> "listen-first" actually saves tokens — including where it doesn't.

## Intent review

The skill is a **listen-first capture mode**. Its whole posture is **deferral**: acknowledge
the session, hold context, and **do nothing** — no file reads, no tool calls, no clarifying
questions, no spec generation — until the user explicitly says "synthesize / update / create."

It has 4 phases:

1. **Initialize**
2. **Listen**
3. **Compare-or-Clarify**
4. **Synthesize-on-request**

…plus a "capture model" of mental buckets it surfaces **only when asked**. The intent is
coherent and the guardrails reinforce it well (e.g. *"if the user says just listen, obey even
if you notice issues"*).

The default with **no switches** is still pure silent listen-first. Questioning and grounding are
strictly opt-in via the switches documented below (and in [`SKILL.md`](./SKILL.md)).

## Does "only listen, summarize at the end" reduce token usage?

**Yes, meaningfully — but understand the mechanism.**

### Where it genuinely saves

- **No tool calls during listening — this is the big one.** The skill forbids browsing/reading
  code while capturing. Tool results (file dumps, greps) are the heaviest token sink because
  they're injected into context **and re-sent on every subsequent turn**. Suppressing them
  during a long capture phase is the largest lever.
- **Short responses + no full spec per message** — cuts output tokens per turn vs. an eager
  agent that drafts a complete requirements doc each time.
- **"Don't expose the whole capture every turn"** — avoids O(n²) growth where the assistant
  restates the entire accumulating capture on every message. Synthesis happens once at the end
  instead of N partial rewrites.
- **No early clarifying loop** — avoids question/answer churn that inflates turns.

### Where it does *not* save (important caveat)

- **It does not compress input context.** Every braindump message stays in the conversation and
  is re-billed as input on each turn — so input tokens still grow linearly and re-accumulate.
  The "mental running capture" isn't real hidden state; the model reconstructs it from history
  each turn. The savings are on **output and tool-result bloat**, not on conversation
  accumulation.
- **Phase 3 (Compare/Clarify) explicitly re-enables tools.** If a user triggers it often, the
  tool-suppression savings erode.
- **Synthesis is one concentrated large pass** — cheaper overall than many partial specs, but
  not free.

### Net

Compared to a default "helpful" agent that eagerly reads files, asks questions, and
re-summarizes each turn, this skill should **reduce total tokens** — primarily by **deferring
all tool use** and **brevity during capture**, not by shrinking context.

## Modes & cost trade-off

The listen-first default can be relaxed with opt-in switches. They sit at very different price
points — the cheap ones add conversational turns; the expensive ones re-enable the tool-result
bloat the default was built to avoid.

| Switch | Cost impact | Why |
|--------|-------------|-----|
| _(none)_ | **Baseline** | Pure silent listen-first. No questions, no tools. |
| `--oktoask` (tool-free questions) | **Small** | A few extra output tokens (the question) + occasional extra turns (re-billing the growing prefix at cache-discounted rates). The big token sink — tool-result bloat — stays OFF. |
| `--thinking` (liberal questioning) | **Moderate** | More questions → more round-trips → more turns re-billing context. Bounded by conversation length. |
| `--oktoreaddocs` | **Large** | Reintroduces exactly what the skill avoids: file/code dumps injected into context and re-sent on every subsequent turn. The heaviest lever. |
| `--oktowebsearch` | **Large** | Web-search result payloads are big and likewise re-billed each turn. |
| Conclusion liberal Q&A | **Small, one-time** | A single bounded burst before synthesis; often *net-negative* cost because it prevents a wrong-synthesis + rework loop. |

**Bottom line:** `--oktoask` and `--thinking` cost little and can pay for themselves by catching
misunderstandings before synthesis. `--oktoreaddocs` and `--oktowebsearch` re-enable the skill's
*primary* savings as a cost — use them deliberately, not by default.
