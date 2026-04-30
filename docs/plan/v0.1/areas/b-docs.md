# Area B: Documentation overhaul

**Status:** ✅ **Done.** Umbrella [#1215](https://github.com/cvoya-com/spring-voyage/issues/1215) closed. B1.1 (V2 framing scrub), B1.2 (audience-led `guide/` restructure: `intro/`, `user/`, `operator/`, `developer/`), B1.3 (audience-led navigation in `docs/README.md`), and B1.4 (`docs-evergreen-framing` CI lint) all shipped. B2.1 (content compression) and B2.2 (`architecture/units.md` decomposition) were deferred behind Areas D / F / G and are tracked as separate backlog items.

Original planning notes preserved below.

---

## Scope (provisional)

- **B1 (early):** audience-split decision (user/operator vs developer); inventory; delete obsolete content; agree on doc-as-code rules.
- **B2 (continuous):** content rewrites that track D / F / G as the system stabilises.

## Dependencies

- B1 depends on: pre-work + (parallel with) A.
- B2 depends on: D, F architectural settling.

## Open questions

- Where do user docs live vs developer docs? Single tree or separate?
- What's the canonical entry point for each audience?
- Which existing docs are immediately deletable vs need rewriting?
- How do we keep docs honest as the system evolves (lint rules, examples-in-CI, etc.)?
- How does this surface intersect with `docs/decisions/` (ADRs) and `docs/plan/v0.1/`?

## Notes

Goal: less verbose, more useful. Avoid duplicating ADRs / area docs / `CLAUDE.md`. Existing `docs/` already has some audience separation (`developer/`, `guide/`) — refine, don't reinvent.
