# Research Domain Package

A domain package that ships agents, unit templates, and skills for research workflows. Use it as-is to spin up a small research team, or as a starting point for your own research-specific orchestration.

## What this package ships

- **Agents** (`agents/`): `researcher`, `literature-reviewer`, `data-analyst`.
- **Units** (`units/`): `research-team` — a hierarchical research cell that routes incoming research requests to the best-fit member.
- **Skills** (`skills/`): `research-triage` (classify incoming research asks and route by expertise), `literature-review` (scope and summarise a body of literature), `data-analysis` (plan and execute data analyses end-to-end).
- **Connectors** (`connectors/`): empty on disk — the research-adjacent connector *implementation* lives alongside the GitHub connector under `src/Cvoya.Spring.Connector.Arxiv/`. It appears automatically in the connector catalogue (`spring connector catalog` / `/connectors`) and exposes the `searchLiterature` tool the `literature-review` bundle declares, so binding a research unit to arxiv self-resolves that bundle's validation warning. (A web-search connector is tracked separately — see [#563](https://github.com/cvoya-com/spring-voyage/issues/563).)

## Using the package

Apply the unit manifest through the same CLI that ships the other domain packages:

```bash
spring apply -f packages/research/units/research-team.yaml
```

Or install through the Phase-6 package browser (`spring package install spring-voyage/research`) once the installer lands on top of the browse surface.

## Shape

The directory layout mirrors the other in-tree domain packages (`packages/software-engineering/`, `packages/product-management/`) so the file-system package catalogue (`GET /api/v1/packages`) exposes it on the CLI (`spring package show research`) and the portal (`/packages/research`) without any extra wiring.
