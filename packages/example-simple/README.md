# example-simple

An ADR-0043 example package that demonstrates the recursive folder shape using **instances only** — no templates, no `from:` clones. Every artefact is authored as a concrete folder under the conventional subdirectories.

Pair it with [`example-templated/`](../example-templated/) — the templated counterpart — to see the difference between literal authoring and template instantiation.

## What installs

Installing this package produces:

- **1 Unit:** `greeting-team`
- **2 Agents:** `friendly-greeter`, `polite-greeter` — both members of `greeting-team`
- **2 Skills:** `say-hello` (owned by `friendly-greeter`), `say-hello-formally` (owned by `polite-greeter`)

The skills sit **inside** their owning agent's folder — that's the ADR-0043 ownership shape. They are not visible to other agents.

## Layout

```
example-simple/
├── package.yaml                              # kind: Package
├── README.md
└── units/
    └── greeting-team/                        # kind: Unit
        ├── package.yaml
        └── agents/
            ├── friendly-greeter/             # kind: Agent
            │   ├── package.yaml
            │   └── skills/
            │       └── say-hello/            # kind: Skill (agent-scoped)
            │           ├── package.yaml
            │           └── say-hello.md
            └── polite-greeter/               # kind: Agent
                ├── package.yaml
                └── skills/
                    └── say-hello-formally/   # kind: Skill (agent-scoped)
                        ├── package.yaml
                        └── say-hello-formally.md
```

## Installing

```bash
spring package install example-simple
```

No `--connector` flag is required — the package ships connector-free.

## Why two agents?

The package demonstrates two literal agent definitions side by side. Each has its own behaviour (one casual, one formal) and ships its own skill. To compare: in [`example-templated/`](../example-templated/) the same fan-out is expressed via a single `software-engineer` template instantiated three times via `from:`.
