# Spring Voyage Documentation

Spring Voyage is a general-purpose AI agent orchestration platform — autonomous AI agents organized into composable groups (units) collaborating on any domain.

## Start with the concepts

Read these regardless of role.

- [Concepts overview](concepts/overview.md) — entry point.
- [Agents](concepts/agents.md), [Units](concepts/units.md), [Connectors](concepts/connectors.md), [Messaging and addressing](concepts/messaging.md), [Initiative](concepts/initiative.md), [Observability](concepts/observability.md), [Packages and skills](concepts/packages.md), [Tenants and permissions](concepts/tenants.md).

## Pick your path

### Using Spring Voyage

You want to interact with agents, send messages, observe activity, manage units.

→ [User guide](guide/user/) and [`cli-reference.md`](cli-reference.md).

### Running Spring Voyage

You want to deploy and operate Spring Voyage — install runtimes, configure connectors, manage secrets, run a tenant.

→ [Operator guide](guide/operator/) and [`cli-reference.md`](cli-reference.md).

### Deploying Spring Voyage from source

You want to check out the repo and run Spring Voyage from source instead of from a packaged release. Once your local instance is up, the operator guide above covers ongoing operations.

→ [Developer guide (deploy from source)](guide/developer/).

### Building on Spring Voyage

You want to extend Spring Voyage — write your own agent runtime, connector, or skill bundle, or work on the platform itself.

→ Top-level [`developer/`](developer/overview.md) tree (extension contracts, packages, secret store) and [`architecture/`](architecture/README.md) (system design).

## Reference

- [`guide/intro/`](guide/intro/overview.md) — short SV introduction; useful before any of the paths above.
- [`cli-reference.md`](cli-reference.md) — concise verb reference for the admin CLI surfaces.
- [`glossary.md`](glossary.md) — definitions of all key terms.
- [`decisions/`](decisions/README.md) — the "why" behind major architectural choices, captured as narrow ADRs.
- [`architecture/`](architecture/README.md) — system design, indexed.
- [`roadmap/`](roadmap/README.md) — forward-looking narrative; live release progress lives on the milestone view.
