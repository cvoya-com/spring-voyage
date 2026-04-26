# Spring Voyage — Guide

Pick the section that matches what you're trying to do.

## [Intro](intro/overview.md)

What Spring Voyage is and how to take your first steps. Start here regardless of role.

- [Overview](intro/overview.md) — what the platform is, what units / agents / connectors are.
- [Getting Started](intro/getting-started.md) — first setup, first unit, first agent.

## [User Guide](user/units-and-agents.md)

Using Spring Voyage day-to-day — sending messages, observing activity, working with units and agents. Both CLI and portal surfaces are covered task-by-task.

- [Managing units and agents](user/units-and-agents.md)
- [Messaging and interaction](user/messaging.md)
- [Observing activity](user/observing.md)
- [Declarative configuration](user/declarative.md) (YAML + `spring apply`)
- [Examples](user/examples.md)
- [Portal walkthrough](user/portal.md)

## [Operator Guide](operator/deployment.md)

Running a Spring Voyage deployment — installing, configuring tenants, runtimes, connectors, secrets. CLI is the canonical mutation surface; the portal exposes read-only views.

- [Deployment](operator/deployment.md) — Docker Compose / Podman self-hosting
- [Secrets](operator/secrets.md)
- [GitHub App setup](operator/github-app-setup.md)
- [BYOI agent images](operator/byoi-agent-images.md)
- [Agent runtimes](operator/agent-runtimes.md)
- [Connectors](operator/connectors.md)

## [Developer Guide](developer/README.md)

Building and running Spring Voyage from source (rather than from a packaged release). Once your local instance is up, the operator guide above covers ongoing operations.

For extending Spring Voyage (writing your own agent runtime, connector, package, etc.), see the top-level [`developer/`](../developer/overview.md) tree.
