# Hello World

The smallest possible package — one unit, one agent, no connector, no skills. Install it to see the install-and-activate flow end to end with a single command, then use it as the skeleton for your own package.

## What this package ships

- **Unit** (`hello-world`) — a single-member orchestrator that routes every incoming message to the greeter agent.
- **Agent** (`greeter`) — acknowledges incoming messages with a short, friendly reply. No tools, no external calls.

Neither declares a `requires:` block, so `spring package install hello-world` succeeds without a `--connector` flag or any inputs.

## Why start here

Every other team in the catalog pulls in a connector, multiple agents, or skills. `hello-world` strips all of that away so you can watch a unit and an agent come up, send the unit a message, and get a reply — the whole lifecycle, nothing else. Once that's clear, the other packages are just more of the same shape.

## Installing

### CLI

```bash
spring package install hello-world
```

No `--connector` flag, no `--input` flags, no other setup.

### Portal

Navigate to `/settings/packages/hello-world` and click **Install**. The wizard renders an empty inputs form (this package declares none) and submits.

## Agent runtime

The unit and agent both use the `claude-code` runtime backed by `claude-sonnet-4-6`, on the `ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest` image — the same baseline the other catalog packages use.

## Policies

- **Initiative**: attentive — agents act on incoming events up to 10 times per hour.
- **Communication**: through-unit — the greeter replies through the unit orchestrator.
- **Work assignment**: capability-match — with one agent in the unit, this is trivially satisfied.
