---
globs: "docs/**,**/AGENTS.md,**/README.md,CONVENTIONS.md"
---

# Documentation discipline

This project documents the system **as it is built**, not as a separate cleanup pass. See [`AGENTS.md`](../../AGENTS.md) and [`CONVENTIONS.md`](../../CONVENTIONS.md) for the full ruleset.

## Quick reference

1. **Docs describe shipped behaviour.** Every claim corresponds to a verifiable surface in the current codebase (function, endpoint, CLI verb, SQL column, YAML key). If you describe behaviour that doesn't exist yet, the doc is wrong.

2. **Aspirational content lives in [`docs/plan/`](../../docs/plan/README.md) or under a Planned callout.** Inside an in-place doc, mark planned content with `> **Planned (vN.M):** …` — name the release or link the tracking issue. Bare future-tense prose ("we plan to") is a violation.

3. **Update docs in the same PR as the code.** When code changes user-visible behaviour, architecture, the database schema, an agent, a convention, or a concept — update the corresponding doc in the same PR. The PR is not complete otherwise.

4. **Record changes via Conventional Commits.** The `[Unreleased]` section of [`CHANGELOG.md`](../../CHANGELOG.md) is generated from Conventional Commits by git-cliff — never hand-edit it; write a conventional commit message instead.

5. **Non-trivial design decisions get an ADR.** Use the `architect` agent for the design pass and the `adr-new` command/skill to scaffold. ADRs live in [`docs/decisions/`](../decisions/README.md).

6. **Living-document headers** stay current. When you do a top-to-bottom verification pass on a doc that has a `Last reviewed: YYYY-MM-DD` header, bump the date.
