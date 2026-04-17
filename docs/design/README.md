# Portal design workflow

This directory tracks design-related documents for the Spring Voyage portal. The operative principle:

**`src/Cvoya.Spring.Web/DESIGN.md` is the contract between design and code. The tool that produced it is the designer's concern, not the repo's.**

## Roles and artefacts

| Role | Works in | Commits |
|---|---|---|
| **Designer** | Any visual design tool (Stitch, Figma, Penpot, sketch+markdown, etc.) | [`src/Cvoya.Spring.Web/DESIGN.md`](../../src/Cvoya.Spring.Web/DESIGN.md) — colour palette, typography, spacing, component patterns, voice & tone |
| **Coding agent / engineer** | This repo | Code that adheres to `DESIGN.md`; updates to `DESIGN.md` when implementation forces a visual-system change |

The designer can use whatever tool reads / writes Google Stitch's [`DESIGN.md` format](https://stitch.withgoogle.com/docs/design-md/overview/) — that schema is a good vendor-neutral contract — but the **choice of tool is not versioned in this repo**. A designer on Stitch, a designer on Figma, and a designer writing plain markdown can all ship the same `DESIGN.md` with the same semantics.

## Why we don't register a design-tool MCP at the repo level

Earlier work considered wiring Stitch's MCP server in this repo's `.mcp.json`. We reverted that (see [#471](https://github.com/cvoya-com/spring-voyage/issues/471) and [PR #478](https://github.com/cvoya-com/spring-voyage/pull/478)). Reasons:

1. **Setup friction for non-designers.** Every contributor would have to set `GOOGLE_CLOUD_PROJECT` and run an OAuth wizard just to boot Claude Code cleanly, even if they never touch portal visuals.
2. **Soft vendor lock-in.** Committing the `.mcp.json` entry to the repo signals "this project uses Stitch" and constrains designers who prefer another tool.
3. **Drift is a process problem, not a tooling problem.** If `DESIGN.md` lags the design source, the fix is "the designer updates `DESIGN.md` when shipping a design change" — same convention as [#424](https://github.com/cvoya-com/spring-voyage/issues/424) for architecture docs. An MCP server would mask drift rather than prevent it.
4. **Coding agents don't need the design tool's native metadata.** `DESIGN.md` is plain, agent-readable markdown — exactly the right level of detail.

If you're a designer using Stitch (or any other tool with an MCP) and want Claude Code to see it in your own sessions, configure that MCP at **user-global scope** (`~/.claude/mcp.json`). That's your tool, not the repo's.

## Designer workflow

1. Explore visual direction in your tool of choice. Reference [`src/Cvoya.Spring.Web/DESIGN.md`](../../src/Cvoya.Spring.Web/DESIGN.md) for the current system so new work stays coherent.
2. When you're ready to ship a change, update `DESIGN.md` to reflect the new system. Commit it on a branch.
3. Open a PR. Reviewers check that `DESIGN.md` is accurate against the shipped portal; coding follow-ups can then pick up the tokens.
4. If the change introduces a brand-new surface (e.g. a new top-level route like Analytics), also reference or update [`docs/design/portal-exploration.md`](portal-exploration.md) so the information-architecture context stays aligned.

## Related documents

- [`portal-exploration.md`](portal-exploration.md) — the plan of record for the portal redesign: IA, key workflows, standalone-vs-hosted, CLI-UI parity.
- [`../../src/Cvoya.Spring.Web/DESIGN.md`](../../src/Cvoya.Spring.Web/DESIGN.md) — the design system itself.
- [`../architecture/cli-and-web.md`](../architecture/cli-and-web.md) — portal architecture.
- [`../../AGENTS.md`](../../AGENTS.md) § "Documentation Updates" — the convention that ships doc updates alongside feature work.
