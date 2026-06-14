---
description: 'Code review standards and intentional design decisions — prevent false positive AI review feedback'
globs: "**"
# Claude Code only: deferred (NOT auto-loaded every session). Injected on review prompts by
# .agents/hooks/code-review-standards-context.sh — saves ~900 tokens on non-review sessions.
# The sentinel below never matches a real file, so Claude's path-scoper skips it at session start.
# Cursor/Copilot still load it always via globs / alwaysApply / applyTo below (they don't run Claude hooks).
paths:
  - ".review-only--injected-via-hook"
applyTo: '**'
alwaysApply: true
---
# Code Review Standards & Design Decisions

Intentional patterns for AI reviewers to prevent false positives. Updated: 2026-03-07

## ADRs

### DR-001: Static Factory Methods for Domain Models

**Decision**: Prefer static factory methods over DI-injected factories for simple domain models.

```csharp
public class DomainModel {
    private DomainModel() { }
    public static DomainModel Create(params) => new() { /* init */ };
}
```

- Use static factories for: pure logic, no external deps, single implementation
- Use DI factories for: service dependencies, multiple implementations
- **DO NOT suggest**: "Use DI factory", "Make constructor public", "Add factory interface"

### DR-002: Hybrid Storage for Operational Logs

Store payloads in BOTH primary database and secondary storage (e.g. object store). Primary = fast access, secondary = archive/fallback. **Not redundancy** - intentional reliability pattern.

### DR-003: Component Tests Cover Domain Logic

Simple domain models tested through repository layer, not isolated unit tests. Unit tests reserved for complex business logic only. Lack of separate unit test files is intentional.

### DR-004: Suppressed Validation for Unlimited String Columns

Suppressing unlimited string length validation (e.g. via ReSharper or analyzer suppressions) is intentional for columns that hold arbitrarily long values (e.g. file paths, URLs, external identifiers). Not a code smell.

### DR-005: Exception-Throwing Properties

Properties MAY throw `InvalidOperationException` on invalid state. Callers check guard property first (e.g., `HasStorage` before `FileName`). This is defensive programming, not control flow.

### DR-006: GitHub Actions `uses:` AI Hallucination

AI reviewers (Gemini) fabricate file paths as action versions. Valid syntax: `@v4`, `@SHA`, `@branch`. File paths (`.cs`, `.sql`) are NEVER valid. **Verify against actual diff before reporting.**

Meta-hallucination: Gemini flagged THIS documentation's example hallucinations as "contradictory examples" - proving the pattern exists.

### DR-007: DbContext Thread Safety

DbContext is NOT thread-safe. `Task.WhenAll` on same DbContext = crash/corruption.

- **Wrong**: `Task.WhenAll` with multiple operations on the same DbContext
- **Correct (default)**: sequential queries on the same DbContext
- **Correct (perf-critical)**: `Task.Run` with a separate `CreateScope()`/DbContext per parallel query

**DO NOT suggest** replacing `Task.Run` with `Task.WhenAll` when DbContext is involved.

### DR-008: No Explicit LangVersion

Modern .NET SDK auto-enables the latest C# version. `<LangVersion>` in csproj is redundant. Do not flag as missing.

## Code Comments Policy

**DO NOT add comments to code** unless explicitly requested by the user.

| Rule | Detail |
|------|--------|
| Self-documenting code | Clear names for variables, methods, classes, parameters |
| XML docs exception | Public API documentation comments are acceptable |
| No `\ No newline at end of file` | Git diff artifact - never add to source files |
| No before/after comments | Code examples should be copy-paste ready; use section headers outside code blocks |

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change |
|:-----|:-------|
| 2026-05-30 | Initial version. |
