# Area J: ADR audit + re-evaluation

**Status:** ✅ **Audit complete.** All ~30 ADRs classified (Stand / Evolve / Retire). Resulting actions shipped: ADR-0028 amended (Decision C — platform-level LLM via the public API, replacing the per-tenant Ollama design), ADR-0029 moved Proposed → Accepted, minor V2.1 framing cleanup elsewhere. The conversation-concept ADR (a gap surfaced by the audit) was deferred to Area F's planning session. Umbrella [#1223](https://github.com/cvoya-com/spring-voyage/issues/1223) can be closed during environment cleanup.

Original planning notes preserved below.

---

## Scope (provisional)

Review every existing ADR in `docs/decisions/`. For each: evolve, retire, or stand. Surface ADRs that need to be written for v0.1 work (ADR-0029 already done via #1202).

## Dependencies

- Depends on: pre-work.
- Blocks: D, F (planning sessions for those areas should consume J's output).

## Open questions

- What's the bar for "retire" vs "evolve"? (Implementation drift? Decision still right?)
- How do we surface ADR debt — list, dashboard, labels?
- Does the ADR template itself need evolution?
- Are there decisions made informally (commits, PR descriptions) that deserve to become ADRs?

## Notes

User explicitly counts on Claude's judgment for which ADRs need evolution. This area drives that proposal. ~30 ADRs currently live in `docs/decisions/0001-*.md` through `docs/decisions/0029-*.md`.
