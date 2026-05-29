# Getting Started

The smallest path: install Spring Voyage, open the portal, and let the new-unit wizard set up your first unit — including the one LLM credential agents need to think. About five minutes after the installer finishes.

Prefer the command line? The [CLI flow](#prefer-the-cli) below does exactly the same thing.

If you'd rather start with the built-in dev-team that picks up GitHub issues, see [Getting Started with Spring Voyage OSS](getting-started-spring-voyage-oss.md).

## 1. Install

If you haven't already, run the one-command installer (Linux or macOS, Podman 4+):

```bash
curl -fSL https://github.com/cvoya-com/spring-voyage/releases/latest/download/install.sh | bash
```

The installer asks for a hostname (default `localhost`) and offers to configure a GitHub App — skip it for this guide. When it finishes, confirm the stack is healthy:

```bash
voyage status
```

It should print a green health check and the web URL.

Full installer flags, TLS, and design notes are in the [operator deployment guide](../operator/deployment.md).

## 2. Create your first unit in the portal

Open the web URL (`http://localhost` by default). With no units yet, the dashboard offers **Create your first unit** — that starts the new-unit wizard:

1. **Name** the unit (e.g. `first-team`) and pick a runtime (Claude Code is the default).
2. **Add an LLM credential.** The wizard prompts for it inline — paste an Anthropic OAuth token (from `claude setup-token`) or an API key, and choose **Save as tenant default** so every unit you create later inherits it. This is the only credential agents need to think, and you set it up right here — no preparation beforehand.
3. **Choose what's inside** — start from a package in the **Catalog**, or from **Scratch** (an empty unit you message directly).
4. **Create**, then **Start** the unit.

The [Web Portal Walkthrough](../user/portal.md) covers every wizard field in detail.

## 3. Talk to it

Open the unit and send it a message from the **Threads** view — e.g. *"Hello — introduce yourself and tell me what you can do."* The reply streams back in the same view, and the **Activity** tab shows what it's doing under the hood.

That's the whole loop: install → wizard → message. From here you can add agent members to turn the unit into a team, wire in connectors, or drive everything from the CLI.

## Prefer the CLI?

The `spring` CLI is the canonical mutation surface — the portal calls the same API. The portal flow above maps to:

```bash
# 1. Store an LLM credential at tenant scope (every unit inherits it).
#    Anthropic OAuth (from `claude setup-token`) is the natural fit for Claude Code:
spring secret create --scope tenant anthropic-oauth --value "<token>"
#    Other supported credentials:
spring secret create --scope tenant anthropic-api-key --value "sk-ant-api03..."
spring secret create --scope tenant openai-api-key    --value "sk-..."

# 2. Create and start a unit (a unit is an agent that can have children).
spring unit create first-team --runtime claude-code
spring unit start first-team
spring unit show first-team        # prints the unit's canonical Guid + status

# 3. Send it a message (use the Guid from `show`).
spring message send unit:<id> "Hello — introduce yourself and tell me what you can do."
spring activity list --source unit:first-team --limit 20

# 4. (Optional) Add an agent member to make it a team.
spring agent create \
    --name ada --role engineer --unit first-team \
    --runtime claude-code \
    --image ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest
```

When you message a unit with members, the unit's runtime decides whether to answer directly, hand the work to a member, or fan it out — that decision lives in the unit's runtime instructions, not in platform configuration (see [ADR-0053](../../decisions/0053-units-are-agents-and-one-way-delivery.md)). See [model providers](../operator/model-providers.md) for the full matrix of runtimes, providers, and credential shapes.

## What's next

- [Spring Voyage OSS quickstart](getting-started-spring-voyage-oss.md) — install the built-in `spring-voyage-oss` package: a ready-made engineering + PM team that picks up work from a GitHub repository.
- [Managing Units and Agents](../user/units-and-agents.md) — full configuration surface for units and agents.
- [Messaging and Interaction](../user/messaging.md) — sending messages on threads.
- [Connectors](../operator/connectors.md) — wire in GitHub, Slack, and other external systems.
- [Declarative configuration](../user/declarative.md) — version-controlled YAML packages you can `spring package install`.
- [Web Portal Walkthrough](../user/portal.md) — the same operations from the browser.
