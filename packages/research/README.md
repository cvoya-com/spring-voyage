# Research

A research team you can spin up to investigate open questions end to end — or use as a starting point for your own research-specific setup.

## What this package ships

- **Unit** (`research-team`) — a team leader that routes each incoming research ask to the best-fit member and holds the thread back to you.
- **Agents:**
  - `researcher` — breaks a question into sub-questions, gathers cited evidence, and synthesises a clear written answer.
  - `literature-reviewer` — surveys a body of work and produces a structured, well-sourced review.
  - `data-analyst` — turns analytical questions into a plan, runs the analysis, and reports findings with caveats.
- **Skills:**
  - `research-triage` — classify an incoming research ask and route it by expertise.
  - `literature-review` — scope and summarise a body of literature.
  - `data-analysis` — plan and execute a data analysis end to end.

## Connectors (optional)

The team works without any connector, and gains two when you bind them:

- **arxiv** — exposes a literature-search tool the literature reviewer uses to build a candidate source list. Binding the unit to arxiv lets the `literature-review` skill resolve its search step automatically.
- **web-search** — general web sourcing for the researcher, backed by a configurable search provider.

Both appear in your connector catalogue (`spring connector catalog` / the portal's **Connectors** page); bind whichever you want the team to use.

## Installing

```bash
spring package install research
```

Or use the portal's **New Unit → Catalog** flow and pick **Research**. Both paths activate the whole team in one atomic install.
