# Hello World Package

> **Tier 1 of 7.** The simplest in-repo catalog package — one unit, one agent, no connector. Pair with [`example-simple`](../example-simple/) to see how the recursive folder layout grows when you add agent-scoped skills.

A minimal connector-free catalog package. Ships one unit (`hello-world`) and one agent (`greeter`) wired up with no `requires:` block on either side, so `spring package install hello-world` succeeds without a `--connector` flag.

## What this package ships

- **Unit** (`units/`): `hello-world` — a single-member orchestrator that routes every incoming message to the greeter agent.
- **Agent** (`agents/`): `greeter` (Greeter) — acknowledges incoming messages with a short friendly reply. No tools, no external calls.

## Why it exists

Every other catalog package shipped today (`software-engineering`, `product-management`, `spring-voyage-oss`, and the OSS sub-units) declares `requires: [{ connector: github }]` on at least one member, which forces `spring package install` callers to supply a `--connector github=…` binding before the install pipeline accepts the package.

E2E tests that exercise the install pipeline itself — not the GitHub connector behaviour — needed a stub binding workaround to get past the connector check. `hello-world` removes that workaround: the install path runs end-to-end with one operator action and no connector configuration.

## Installing

### CLI

```bash
spring package install hello-world
```

No `--connector` flag, no `--input` flags, no other side-channel setup.

### Portal

Navigate to `/settings/packages/hello-world` and click **Install**. The wizard renders an empty inputs form (this package declares none) and submits.

## Agent runtime

The unit and agent both use the `claude-code` runtime backed by `claude-sonnet-4-6`, so the install Phase-2 activator finds a runtime it knows how to start. The container image is `ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest`, the same baseline the other catalog packages use.

## Policies

- **Initiative**: attentive — agents act on incoming events up to 10 times per hour.
- **Communication**: through-unit — the greeter replies through the unit orchestrator.
- **Work assignment**: capability-match — only one agent in the unit, so this is trivially satisfied.
