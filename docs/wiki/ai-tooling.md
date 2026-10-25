# AI Tooling

## Approach

The development of this solution deliberately demonstrates an AI-agnostic approach to developer tooling. The goal was not to pick a favourite — it was to understand what each tool and its underlying models are genuinely best suited for across a real delivery.

## Tools Used

| Tool | Primary Role |
|---|---|
| **Claude Code** (Anthropic) | Spec-driven generation, architectural reasoning, primary code authoring |
| **OpenAI Codex** | Code generation, pull request workflow automation, agentic task execution |
| **GitHub Copilot** (web agent) | In-editor assistance, agentic task execution, pull request review participation |

All three tools share a common context through the `.agents/` folder — rules, conventions, and prompt templates are maintained in one place and symlinked per tool.

## Key Findings

- Cross-model review (writing with one tool, reviewing with another) caught assumptions and patterns the authoring model would have missed.
- Claude Code performed strongest on spec-to-code generation when given structured HLDs and NFRs.
- Codex was well-suited to PR workflow automation and repetitive generation tasks.
- GitHub Copilot's web agent added value in PR review participation — visible in the PR conversation history.

## Embedded AI Agent

A Claude SDK-powered conversational agent is embedded directly in the API. It allows natural-language queries against the buildability data — for example: *"Which sets can brickfan35 build?"* — rather than requiring direct API calls. This was a deliberate side quest to explore agentic integration as a pattern for data-rich APIs.

## Recommendations (for teams adopting this approach)

- Run AI reviews alongside linters — they serve different purposes.
- Use a different model for review than for authoring. Cross-model review is more effective.
- Invest in shared context files (`.agents/` equivalent) early. All tools benefit from a single source of truth on conventions and domain knowledge.

## Setup

See the `.agents/` folder for full configuration. Run the appropriate setup script after cloning to recreate symlink aliases:

```bash
# Mac / Linux
bash .agents/setup/scripts/agents-setup.sh

# Windows (PowerShell — run as Administrator)
.\.agents\setup\scripts\agents-setup.ps1
```

## Further Reading

- [Architecture](architecture.md) — solution structure and design decisions
- [Testing Strategy](testing.md) — test levels and infrastructure
