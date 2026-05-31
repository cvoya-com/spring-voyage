# Getting Started with Spring Voyage OSS

The `spring-voyage-oss` package ships built-in with the platform. It's a single unit with a small team attached: five engineer agents and two program-manager agents. When you install it against a GitHub repository, the team picks up issues and pull requests automatically — opening PRs, triaging issues, wiring sub-issue / blocked-by relationships, and reporting back.

This guide walks you from a freshly-installed Spring Voyage to that team actively working on a repo of your choice. About 15–20 minutes the first time, mostly waiting on GitHub.

For the conceptual picture — what each role does, why the team is structured this way — see [Spring Voyage OSS concept](../../concepts/spring-voyage-oss.md). For the operator-detail reference (deeper troubleshooting, the package internals), see [Install and run the Spring Voyage OSS dogfooding unit](../operator/dogfooding-oss-unit.md).

## Prerequisites

- **Spring Voyage installed and running.** Follow [Install](../../../README.md#install) if you haven't, then `voyage status` should show green.
- **An LLM credential at tenant scope.** The engineer and PM agents run on Claude Code, so an `anthropic-oauth` secret is the natural fit. Set it via the portal (Settings → Tenant defaults, or any unit-creation wizard), or from the CLI if you don't have it set yet:

  ```bash
  spring secret create --scope tenant anthropic-oauth --value "<token from `claude setup-token`>"
  ```

- **A GitHub repository for the team to work on.** Anything you have admin access to. The team will open PRs and issues against it, so a dedicated dev / playground repo is a good starting point.

## 1. Register the GitHub App

Spring Voyage authenticates against GitHub as a GitHub App that your deployment owns. Register one (one-time per deployment):

```bash
spring github-app register
```

This opens a browser, drives GitHub's manifest flow with a single click, writes the App credentials into `~/.spring-voyage/spring.env`, and prints the App's public install URL. Visit the install URL and **install the App on your target repository** via the GitHub UI.

After installation, **note the installation ID** — it's the numeric suffix in the URL of the App's installation settings page (`.../installations/<id>`). You'll pass it to the package install in step 3.

Full registration detail (private repo flow, dev vs. prod app separation): [Register your GitHub App](../operator/github-app-setup.md).

## 2. Start the webhook forwarder

GitHub delivers webhooks to publicly-reachable URLs only — your local `http://localhost` install can't receive them directly. The recommended fix uses the `gh` CLI's built-in webhook-forwarding API. The installer bundles a wrapper that you invoke through `voyage`:

```bash
# One-time: install the gh-webhook extension and authenticate gh.
gh extension install cli/gh-webhook
gh auth login

# Start forwarding webhooks from your repo to the local API.
voyage gh-webhook-forward --repo your-org/your-repo
```

`voyage gh-webhook-forward` reads `GitHub__WebhookSecret` from your install's `spring.env` so signatures match what the platform verifies, and points the forwarder at `http://localhost:8080/api/v1/webhooks/github` by default. Pass `--url` to override the destination or `--events` to narrow the event set; see `voyage gh-webhook-forward --help` for the full flag list.

Leave the forwarder running in its own terminal. Stop it with Ctrl-C when you're done; GitHub tears down the short-lived forwarding hook automatically.

Other forwarder options (smee.io, cloudflared, an SSH reverse tunnel for hosts without `gh auth`) are documented in [Local-dev recipe](../operator/github-app-setup.md#local-dev-recipe).

## 3. Install the package

```bash
spring package install spring-voyage-oss \
  --input github_repo=your-org/your-repo \
  --input github_installation_id=<installation-id>
```

This atomically creates the `spring-voyage-oss` unit with all seven agent members, wires the GitHub connector binding, and prints the install ID. Inspect it:

```bash
spring package status <install-id>
spring unit show spring-voyage-oss
```

The unit should reach `Running` and each agent member `Validating` → `Running` over the next minute or two as their container images start.

You can also do this from the portal: open `/units/create`, choose **Catalog**, pick **Spring Voyage OSS**, fill in the two inputs.

## 4. Use the `spring-voyage-team` label

The team only acts on issues and pull requests **you explicitly mark for it.** That gate is the `spring-voyage-team` label:

- **Apply it to an issue:** signals "the Spring Voyage OSS team should own this." The unit's leader reads the issue, decides whether it's an implementation task (→ engineer) or needs triage first (→ PM), and delegates.
- **Remove it:** the team stops following that issue. No further actions are taken in response to comments, labels, or status changes on it.
- **Auto-applied on team output:** when a team agent opens a PR or files an issue on its own initiative, the label is applied automatically so the unit sees the resulting feedback loop. You don't need to apply it manually for those.

In practice: open an issue in your repo, add the `spring-voyage-team` label, and within a few seconds you should see activity on the unit:

```bash
spring activity list --source unit:spring-voyage-oss --limit 20
```

If the issue carries an `area:*` label too (e.g. `area:platform`, `area:web`), the leader hands it straight to an engineer whose expertise matches. Without an area label, a PM agent triages first and re-routes once an area is set.

## 5. First test — end-to-end

The cleanest smoke test is a real webhook delivery:

1. In your repo on GitHub, open a new issue with a clear, small ask — e.g. *"Add a small typo fix to README.md: `recieve` → `receive`."*
2. Add the `spring-voyage-team` label.
3. Watch `spring activity list --source unit:spring-voyage-oss --limit 20`. You should see the leader receive the `label_change` event, delegate to an engineer agent, and within a few minutes the engineer opens a PR against your repo.

If you'd rather skip GitHub and send a direct prompt:

```bash
spring message send unit:<unit-id> \
  "New issue opened: 'README has a typo: recieve should be receive.' Take a look and propose a fix."
```

The unit's leader will pick an engineer, delegate the work, and the engineer's container will clone the repo, draft a change, and report back.

## Troubleshooting

| Symptom | Likely cause | Resolution |
| ------- | ------------ | ---------- |
| Package install fails with `"GitHub App not configured"` | App credentials never made it into `spring.env`, or the App isn't installed on the target repo | Re-run `spring github-app register`; confirm the App is installed on the repo on github.com. `spring system configuration "GitHub Connector"` reports the current state. |
| Webhooks open the issue but the unit doesn't react | Forwarder not running, or `spring-voyage-team` label not applied | Confirm `voyage gh-webhook-forward` is still in its terminal; confirm the label is on the issue (`gh issue view <n> --json labels`). |
| Agent stuck in `Validating` | Image pull failed | `podman images \| grep spring-voyage-agent-oss` — if missing, the GHCR pull is still in progress or failed; check `voyage logs spring-worker`. |
| Engineer agent opens a PR but the unit never sees the review comments | The PR doesn't carry `spring-voyage-team` | The team's instruction is to auto-label its own output; if a PR is missing the label, add it manually (`gh pr edit <n> --add-label spring-voyage-team`) and the feedback loop resumes. |

Deeper troubleshooting and the package's design rationale are in the [operator dogfooding guide](../operator/dogfooding-oss-unit.md).

## What's next

- [Spring Voyage OSS concept](../../concepts/spring-voyage-oss.md) — what the unit is and how the leader decides which agent to delegate to.
- [Connectors](../operator/connectors.md) — wire additional connectors (Slack, more GitHub repos) onto the same or new units.
- [Packages](../user/declarative.md) — define your own packages with the same shape so your custom teams can be installed atomically.
- [Managing Units and Agents](../user/units-and-agents.md) — tune the team, add or remove members, swap models per agent.
