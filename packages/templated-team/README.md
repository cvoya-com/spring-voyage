# Templated Team

A starter package that shows how to build a team from **reusable templates** — define an agent or unit once, then stamp out as many running copies as you need. Install it as-is for a small engineering team, or use it as a pattern for your own template-based packages.

If you're new to packages, read [`hello-world`](../hello-world/) first for the minimal shape, then come back here to see how templates remove duplication.

## Type vs instance

A **template** (`kind: UnitTemplate` / `kind: AgentTemplate`) is a definition that does **not** run on its own — it exists to be cloned. A concrete `kind: Unit` or `kind: Agent` that declares `from: <template-name>` is the running instance. At install time the platform copies the template, gives the instance its own identity, and applies any overrides the instance declares on top.

## What installs

Installing this package produces one unit and five agents:

```
platform-eng (Unit, from: engineering-team)
├── team-lead         (Agent, stamped from the engineering-team template)
├── senior-engineer   (Agent, stamped from the engineering-team template)
├── ada               (Agent, from: software-engineer)
├── hopper            (Agent, from: software-engineer)
└── lovelace          (Agent, from: software-engineer)
```

- `platform-eng` is a concrete unit that inherits its orchestration prompt and policies from the `engineering-team` template and stamps the template's nested children (`team-lead`, `senior-engineer`).
- `ada`, `hopper`, and `lovelace` are three concrete agents, each `from: software-engineer` — three thin references to one shared definition rather than three copies of the same content.

Each agent gets its own identity, so two installs of this package produce distinct teams even though the agent names repeat.

## Layout

```
templated-team/
├── package.yaml
├── README.md
├── templates/
│   ├── software-engineer/                    # kind: AgentTemplate
│   │   ├── package.yaml
│   │   └── README.md
│   └── engineering-team/                     # kind: UnitTemplate (with stamped children)
│       ├── package.yaml
│       └── agents/
│           ├── team-lead/                    # stamped at install
│           │   └── package.yaml
│           └── senior-engineer/              # stamped at install
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

## Override semantics

When an instance declares `from:`, it inherits the template's fields and can override them:

- **Scalars** (e.g. `instructions:`) — the instance value wins; the template fills the gap when the instance omits it.
- **Maps** (e.g. `ai: { runtime, model }`) — deep-merged, with instance keys winning.
- **Lists** (e.g. `expertise:`, `requires:`) — the instance replaces the template's list entirely.
- **Members** — if the instance omits `members:`, the template's stamped child tree is the member set. If the instance declares its own `members:`, that list replaces the stamped tree.

The three engineers (`ada`, `hopper`, `lovelace`) each override only their `description` while inheriting `ai:`, `role:`, `capabilities:`, `instructions:`, and `expertise:` from the `software-engineer` template.

## Installing

```bash
spring package install templated-team
```

The package ships connector-free, so no `--connector` flag is needed.

## Why use templates?

Three engineers from one definition is three short `from:` references, not three folders of duplicated content. Edit the template's instructions once and every future install picks up the change — useful whenever you want several agents (or several teams) to stay in lockstep.
