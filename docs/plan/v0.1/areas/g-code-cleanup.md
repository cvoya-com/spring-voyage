# Area G: Code review + decomposition

**Status:** Planning session pending. Splits into discovery (early) and PRs (later).

## Scope (provisional)

- **Discovery (early, parallel):** review existing code, identify areas needing cleanup or decomposition.
- **PRs (later):** targeted cleanup PRs once D establishes new boundaries.

## Dependencies

- Discovery depends on: pre-work.
- Cleanup PRs depend on: D (so the new boundaries inform decomposition direction).

## Open questions

- What heuristics define "needs cleanup"? (size, coupling, test coverage, divergence from new boundaries)
- Should cleanup land in v0.1 or some carry to post-v0.1?
- How do we avoid getting lost in cleanup that doesn't pay for itself?
- Where does "code health" reporting live (a doc, a dashboard, an issue label)?

## Notes

Bias toward cleanup that aligns with D's new boundaries; defer cosmetic cleanup. Output of discovery feeds the per-area umbrellas as sub-issues.
