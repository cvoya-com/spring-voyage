# example-templated

An ADR-0043 example package that demonstrates **type / instance separation** via templates (ADR-0043 §5). Pair with [`example-simple/`](../example-simple/) — the instance-only counterpart — to compare authoring styles.

## Type vs instance

A **template** (`kind: UnitTemplate` / `kind: AgentTemplate`) is an artefact folder that does **not activate**. It exists to be cloned. A concrete `kind: Unit` or `kind: Agent` that declares `from: <template-name>` is the instance — at install time the resolver walks the template's folder tree, stamps out fresh concrete artefacts with newly minted identities, and merges the consumer's overrides per ADR-0043 §5d.

## What installs

Installing this package produces:

- **1 Unit:** `platform-eng` — concrete, declares `from: engineering-team`
  - Inherits the orchestrator prompt and policies from the template
  - Stamps fresh copies of the template's nested children (`team-lead`, `senior-engineer`)
  - Adds three explicit `from: software-engineer` instances on top: `ada`, `hopper`, `lovelace`

Final activated tree:

```
platform-eng (Unit)
├── team-lead         (Agent, stamped from engineering-team's children)
├── senior-engineer   (Agent, stamped from engineering-team's children)
├── ada               (Agent, from: software-engineer template)
├── hopper            (Agent, from: software-engineer template)
└── lovelace          (Agent, from: software-engineer template)
```

Each agent gets a fresh Guid identity per ADR-0036. Two `team-lead`-named agents from two installs are distinct entities.

## Layout

```
example-templated/
├── package.yaml                              # kind: Package
├── README.md
├── templates/
│   ├── software-engineer/                    # kind: AgentTemplate (archetype)
│   │   ├── package.yaml
│   │   └── README.md
│   └── engineering-team/                     # kind: UnitTemplate (archetype with stamped children)
│       ├── package.yaml
│       └── agents/
│           ├── team-lead/                    # kind: Agent — cloned at install
│           │   └── package.yaml
│           └── senior-engineer/              # kind: Agent — cloned at install
│               └── package.yaml
└── units/
    └── platform-eng/                         # kind: Unit, from: engineering-team
        ├── package.yaml
        └── agents/
            ├── ada/                          # kind: Agent, from: software-engineer
            │   └── package.yaml
            ├── hopper/                       # kind: Agent, from: software-engineer
            │   └── package.yaml
            └── lovelace/                     # kind: Agent, from: software-engineer
                └── package.yaml
```

## Override semantics (ADR-0043 §5d)

- **Scalars** (e.g. `instructions:`) — instance wins, template fills gaps.
- **Maps** (e.g. `ai: { runtime, model }`) — deep merge with instance keys winning.
- **Lists** (e.g. `expertise:`, `requires:`) — instance replaces template entirely.
- **Members** — the template's stamped child tree is the member set when the instance omits `members:`. If the instance declares its own `members:`, that list replaces the stamped tree entirely (per-member overrides are deferred to a later ADR).

The three concrete agents (`ada`, `hopper`, `lovelace`) demonstrate scalar override on `from: software-engineer` — each can tweak its own description while inheriting `ai:`, `role:`, `capabilities:`, and `instructions:` from the template.

## Installing

```bash
spring package install example-templated
```

The package ships connector-free, so no `--connector` flag is needed.

## Why use templates?

Three software-engineer instances from one definition is three thin `from:` references, not three folders of duplicated content. Edit the template's `instructions:` once and every future install picks up the change. (Existing installs snapshot-bind per ADR-0043 §5f — re-installs pick up the new shape.)
