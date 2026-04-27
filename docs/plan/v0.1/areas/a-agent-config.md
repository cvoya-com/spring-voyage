# Area A: Coding-agent config

**Status:** ✅ **Done.** Umbrella [#1214](https://github.com/cvoya-com/spring-voyage/issues/1214) closed. Subsequent agent-config changes flow as ad-hoc PRs under ongoing hygiene rather than a tracked area.

What shipped: V2 / V2.1 framing scrubbed and `AGENTS.md` / `CONVENTIONS.md` generalised; `CLAUDE.md` collapsed to a pointer; sub-agents slimmed and three new ones added (`web-engineer`, `cli-engineer`, `architect`); four project skills added (`/web`, `/openapi-diff`, `/triage`, `/adr-new`); language LSPs pinned at repo scope; user-level `~/.claude/CLAUDE.md` made project-agnostic; `gh-app` script extended with `--blocked-by` / `--blocking` / `--sub-issue-of` plus `issue link / close / reopen` subcommands.

Original planning notes preserved below for context.

---

## Scope (provisional)

Review and evolve user/repo `CLAUDE.md`, `AGENTS.md`, `CONVENTIONS.md`, `.claude/agents/`, skills, plugins, MCPs. Cleanup, dedupe, resolve contradictions. Recommended early because it raises signal-to-noise for every other area.

## Dependencies

- Depends on: pre-work (milestone rename, label creation).
- Blocks: nothing hard, but precedes other areas in practice.

## Open questions

- Which existing rules survive? Which are retired?
- How do we handle conflicts between user-level and repo-level guidance?
- Is the milestone-only-no-umbrella rule still right (now amended for area umbrellas)?
- Are current agent definitions / skills / plugins all still useful?
- What's the right scope split between `CLAUDE.md`, `AGENTS.md`, and `CONVENTIONS.md`?

## Notes

User has flagged that everything in this surface is candidate for evolution; nothing is treated as immutable input.
