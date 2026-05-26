# Getting Started

The smallest possible path: install Spring Voyage, drop in one LLM credential, create a unit, talk to it. About five minutes after the installer finishes.

If you'd rather start with the built-in dev-team that picks up GitHub issues, see [Getting Started with Spring Voyage OSS](getting-started-spring-voyage-oss.md).

## 1. Install

If you haven't already, run the one-command installer (Linux or macOS, Podman 4+):

```bash
curl -fSL https://github.com/cvoya-com/spring-voyage/releases/latest/download/install.sh | bash
```

The installer asks for a hostname (default `localhost`) and offers to configure a GitHub App — skip it for this guide. When it finishes:

```bash
voyage status
```

Should print a green health check and the web URL. Open it in your browser — the portal is the same operations you'll do from the CLI below.

Full installer flags, TLS, and design notes are in the [operator deployment guide](../operator/deployment.md).

## 2. Drop in an LLM credential

Agents need an LLM to think with. The simplest choice is Anthropic's Claude Code, authenticated via an OAuth token (Spring Voyage runs the `claude-code` CLI on your behalf, so the OAuth flow is the natural fit).

Generate the token (`claude setup-token` in a terminal where Claude Code is installed) and store it at tenant scope so every unit you create inherits it:

```bash
spring secret create --scope tenant anthropic-oauth --value "<token>"
```

Verify:

```bash
spring secret list --scope tenant
```

Other supported credentials:

```bash
# Anthropic API key (for the platform's own LLM calls and the spring-voyage agent runtime)
spring secret create --scope tenant anthropic-api-key --value "sk-ant-api03..."

# OpenAI API key (for codex or OpenAI-powered agents)
spring secret create --scope tenant openai-api-key --value "sk-..."
```

See [model providers](../operator/model-providers.md) for the full matrix of runtimes, providers, and credential shapes.

## 3. Create your first unit

A unit *is* an agent that has children, and at the start it has none — that's fine. Give it a runtime so it knows how to think:

```bash
spring unit create first-team --runtime claude-code
```

Start it:

```bash
spring unit start first-team
```

Verify it reached `Running`:

```bash
spring unit show first-team
```

## 4. Send it a message

The `show` output above prints the unit's canonical `Guid`. Send a message to that address:

```bash
spring message send unit:<id> "Hello — introduce yourself and tell me what you can do."
```

Watch the activity stream while it works:

```bash
spring activity list --source unit:first-team --limit 20
```

The same conversation is visible in the portal under **Units → first-team → Threads**.

## 5. Add an agent member (optional)

A unit by itself is a useful conversational partner. To make it a *team*, attach members:

```bash
spring agent create \
    --name ada \
    --role engineer \
    --unit first-team \
    --runtime claude-code \
    --image ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest
```

When you message the unit again, the unit's runtime decides whether to answer directly, hand the work to Ada, or fan it out. That decision lives in the unit's runtime instructions, not in platform configuration — see [ADR-0053](../../decisions/0053-units-are-agents-and-one-way-delivery.md) for the model.

## What's next

- [Spring Voyage OSS quickstart](getting-started-spring-voyage-oss.md) — install the built-in `spring-voyage-oss` package: a ready-made engineering + PM team that picks up work from a GitHub repository.
- [Managing Units and Agents](../user/units-and-agents.md) — full configuration surface for units and agents.
- [Messaging and Interaction](../user/messaging.md) — sending messages on threads.
- [Connectors](../operator/connectors.md) — wire in GitHub, Slack, and other external systems.
- [Declarative configuration](../user/declarative.md) — version-controlled YAML packages you can `spring package install`.
- [Web Portal Walkthrough](../user/portal.md) — the same operations from the browser.
