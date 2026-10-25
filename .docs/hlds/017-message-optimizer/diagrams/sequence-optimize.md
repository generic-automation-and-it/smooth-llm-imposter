# Per-message flow

One request at the forward seam. Every failure path ends in a forward; none ends in an error.

```mermaid
sequenceDiagram
    participant C as Agent client
    participant K as Coordinator
    participant T as Tool strategy
    participant S as Message strategy
    participant G as Decision gate
    participant O as Optimizer LLM
    participant U as Upstream
    C->>K: Request (switches handled, routing transforms applied)
    par tools
        K->>T: Same body
        T-->>K: Edit to tools/tool_choice, or no change
    and messages
        K->>S: Same body
        loop each human message, in series
            S->>S: Remembered? replay and continue. Short or harness-only? continue
            S->>G: Can this message be optimized? (skipped if bypassed or circuit open)
            alt negative answer
                G-->>S: No
                S->>S: Record original; skip optimizer
            else positive or failure
                G-->>S: Probability, or failure → continue to optimizer
                S->>O: Instructions + this message only (skipped if circuit open)
                O-->>S: Rewrite, or failure
                S->>S: Validate; one targeted retry at most; record outcome
            end
        end
        S-->>K: Edits to message text, or no change
    end
    K->>U: Original body with both edits merged
    U-->>C: Inference stream (unchanged)
```
