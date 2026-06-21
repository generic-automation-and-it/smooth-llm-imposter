# HLD Status Vocabulary

Shared lifecycle vocabulary for discovery/prototyping HLDs. Use consistently across
README, AGENTS.md, LADRs, and NFRs.

| Term | Applies to | Meaning |
|---|---|---|
| **In Discovery** | HLD (README metadata) | The initiative is in discovery; decisions and quality spec are still in flux. |
| **Completed** | HLD (README metadata) | The design is implemented and shipped in `src` (verified against code/tests). Terminal state. |
| **Cancelled** | HLD (README metadata) | The initiative was abandoned without shipping. Keep the HLD for history; note why at the top. Terminal state. |
| **Draft** | LADR / NFR | Under active discovery. Directional but not yet validated. Default in an in-discovery HLD. |
| **Prototype** | LADR / NFR | Directional, under active validation. Work may begin against it; treat as load-bearing but flag deviations. |
| **Accepted** | LADR / NFR | Locked in. Implementations can rely on it. |
| **Superseded by LADR-MM** | LADR | Replaced; keep the file and add a supersession note at the top. |
| **Deprecated** | LADR | Capability removed; keep the file for history. |
| **TBD** | Anywhere | Acknowledged unknown. Must have an owner or a trigger to resolve. |

`Draft → Prototype → Accepted` is the typical progression. At discovery phase, new LADRs and
NFRs default to **Draft**. A well-validated design may open directly at **Prototype**.

## Strategic vs tactical LADRs

- **Strategic** (LADR-01..N) — *what* we are building and *why*.
- **Tactical** (LADR-N+1..M) — *how* we will execute (runtime, protocol, config choices).

Number strategic first; never renumber when adding tactical LADRs.

## Conventions

- Dates ISO 8601 (`YYYY-MM-DD`). Convert relative dates ("next sprint") to absolute before saving.
- Present tense for current state; future tense only for unshipped capability.
- No `[TODO]` placeholders in finalised sections — use `TBD` with an owner/trigger.
