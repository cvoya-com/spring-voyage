# Register your GitHub App

Spring Voyage's GitHub connector authenticates as a **GitHub App that the operator owns and registers themselves**. Spring Voyage does **not** ship a shared App private key, and there is no central `api.spring-voyage.com` callback that brokers installs through us. Each deployment registers its own App, and the App's webhook URL, callback URL, App ID, slug, and private key all belong to that deployment.

This is the same model Renovate, Sentry self-hosted, Linear self-hosted, and Probot apps use. The trade-offs are worth saying out loud:

- A shared private key would have to ship with our binary or be fetched from a Spring-Voyage-hosted service. Either is a security non-starter for a self-hostable platform.
- Per-deployment Apps mean per-deployment webhook deliveries, rate-limit budgets, audit logs, and branding (the App name shown to repo owners is the operator's choice).
- A leaked key in one deployment cannot affect any other deployment.
- The webhook URL, callback URL, and setup URL are all owned by the operator — no fragile redirect dance through a Spring-Voyage-controlled domain.

This page is the operator-facing companion to [Deployment guide § Tier-1 platform credentials](deployment.md#tier-1-platform-credentials--github-app-identity-env-only). Pick **one** of the two paths below; both produce the same set of values in `eng/config/spring.env`.

> **Do you even need the App?** If a unit only needs to act on a repository you do **not** own — e.g. contributing to an open-source project — a PAT is simpler and requires no App registration and no OAuth. See [GitHub connector auth options](github-connector-auth.md) to choose. Register the App when you operate the repo and want a bot identity (and, on a public deployment, an active webhook).

## Document map

- [Path A — `spring github-app register` (recommended)](#path-a--spring-github-app-register-recommended) — one CLI verb that drives GitHub's [App-from-manifest flow](https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest), opens the pre-filled "create App" page, captures the conversion code on a loopback listener, and writes the env file for you.
- [Path B — Manual registration](#path-b--manual-registration) — point-and-click in `github.com` if you want to inspect every field or you cannot run the CLI on the host that owns `eng/config/spring.env`.
- [Required values](#required-values) — the shape of every field, regardless of which path you took.
- [Local-dev recipe](#local-dev-recipe) — register a separate "dev" App pointed at `http://localhost:*` URLs.
- [Verifying the install](#verifying-the-install) — confirm the connector picks up the credentials and the App can mint installation tokens.

## Path A — `spring github-app register` (recommended)

If you already have the `spring` CLI on the same host as `eng/config/spring.env`, this is the shortest path:

```bash
cd /path/to/spring-voyage
spring github-app register --name "Spring Voyage (<your-deployment>)"
```

The verb:

1. Builds a manifest scoped to **your** deployment URLs (webhook + OAuth callback), carrying the App's name, permissions, and webhook-event subscriptions.
2. Binds a loopback HTTP listener on `127.0.0.1:<ephemeral-port>` — it both serves the hand-off page in the next step and receives the conversion callback.
3. Opens your browser at that local page (`http://127.0.0.1:<ephemeral-port>/`), which immediately POSTs the manifest to `https://github.com/settings/apps/new` (or `https://github.com/organizations/<org>/settings/apps/new` when you pass `--org <slug>`) — GitHub renders the "create App" confirmation page **pre-filled** with the right name, permissions, events, callback URL, and webhook URL. You click **Create**. (GitHub's manifest flow accepts the manifest only as a `POST` form field, so the CLI hands it off through this local page rather than linking straight to a pre-filled `github.com` URL.)
4. GitHub redirects back to the loopback listener with a one-time conversion `code`.
5. The CLI exchanges the code via `POST /app-manifests/{code}/conversions` and receives `app_id`, `slug`, `pem`, `webhook_secret`, `client_id`, and `client_secret` back in the response.
6. The CLI writes `GitHub__AppId`, `GitHub__AppSlug`, `GitHub__PrivateKeyPem` (single-quoted, single-line, with literal `\n` between blocks), and `GitHub__WebhookSecret` to `eng/config/spring.env`. Pass `--write-secrets` to persist the same values as platform-scoped secrets via the registry instead of the env file.

Restart the platform after the file changes (`./deploy.sh restart` for Podman, `docker compose --env-file ../config/spring.env up -d` from `eng/deploy/` for Compose) so the connector picks up the new credentials.

Run `spring github-app register --help` for the full flag list, including `--org`, `--write-env`, `--write-secrets`, `--env-path`, `--webhook-url`, `--oauth-callback-url`, and `--manual`.

> **Deployment URLs.** The webhook URL and the App's user-OAuth `callback_urls` default to the CLI's configured endpoint (`SPRING_API_URL` / `~/.spring/config.json`, falling back to `http://localhost:5000`). On a real deployment, pass your public origin so GitHub can reach them — e.g. `--webhook-url https://<your-host>/api/v1/webhooks/github --oauth-callback-url https://<your-host>/api/v1/tenant/connectors/github/oauth/callback`. **`install.sh` does this for you**, deriving both from `DEPLOY_HOSTNAME` and the resolved Caddy HTTPS port so the App's `callback_urls` matches `GitHub__OAuth__RedirectUri` exactly.
>
> **Localhost installs:** GitHub refuses to register an App whose webhook isn't publicly reachable, and requires a webhook whenever events are subscribed. So when the webhook URL is `localhost` / loopback, the CLI registers the App **without a webhook or event subscriptions** — a valid API-only App. Local dev then receives events via `gh webhook forward` (below), which is independent of the App's own webhook.

### No browser on the host (headless / remote server)

On a server with no browser — a VPS, a container, an SSH session with no display — the CLI can't open GitHub for you, and your laptop can't reach the host's loopback listener. The verb detects this automatically (or pass `--manual` to force it) and switches to a copy/paste flow:

1. It writes the **pre-filled** manifest form to `spring-github-app-register.html` next to your `spring.env` and prints the path.
2. Copy that file to a machine that has a browser and open it — e.g. `scp <your-host>:~/.spring-voyage/spring-github-app-register.html .`, then open the file. The form POSTs the pre-filled manifest to GitHub; click **Create**.
3. GitHub redirects to a `http://127.0.0.1:<port>/?code=…` URL that won't load on a remote host — that's expected. Copy the whole redirect URL (or just the `code=` value) from your browser's address bar.
4. Paste it back at the CLI prompt. The CLI exchanges the code and writes the credentials exactly as the browser flow does.

A future release will streamline this with an SSH-tunnel option that captures the code automatically.

## Path B — Manual registration

Use this path when you cannot run the CLI on the host (e.g. an air-gapped operator running the CLI elsewhere), or you want to review every field GitHub asks for.

### 1. Decide where the App lives

GitHub Apps belong either to your personal account or to an organisation:

- **Personal:** `https://github.com/settings/apps/new`
- **Organisation:** `https://github.com/organizations/<org-slug>/settings/apps/new` (you must have the **Owner** role on the org).

Pick the organisation account when more than one person on your team needs to manage the App's settings, rotate its private key, or read its webhook deliveries — App ownership is account-level, not user-level.

### 2. Fill in the App settings

GitHub's "Register new GitHub App" page asks for the following. Substitute your deployment's public hostname (the FQDN you set as `DEPLOY_HOSTNAME` in `eng/config/spring.env`) wherever the table says `<your-host>`:

| Field | Value |
|-------|-------|
| **GitHub App name** | A globally-unique name on github.com. `Spring Voyage (<your-deployment>)` works. The name appears in PR comments, issue assignments, and on every repo install screen — pick something operators on the receiving end will recognise. |
| **Description** | Free text. Operators see this on the install screen. |
| **Homepage URL** | Your portal URL — e.g. `https://<your-host>/`. |
| **Callback URL** | `https://<your-host>/connectors/github/installed` (the post-install destination on the portal). For local dev: `http://localhost:5173/connectors/github/installed`. |
| **Setup URL** | Same as the Callback URL. Tick **Redirect on update**. GitHub uses this URL after every install, re-install, and version-bump. |
| **Webhook → Active** | Tick. |
| **Webhook URL** | `https://<your-host>/api/v1/connectors/github/webhooks` — the connector's webhook ingress endpoint behind Caddy. |
| **Webhook secret** | Generate a strong random value (`openssl rand -hex 32` is fine). Save it — you will paste it into `GitHub__WebhookSecret` in step 5. |

### 3. Grant the required permissions

Under **Repository permissions**, set exactly these scopes (no more — every extra scope adds blast radius if the key leaks):

| Permission | Access | Why |
|------------|--------|-----|
| **Contents** | Read-only | Read repository files for context (`README.md`, source files referenced from issues / PRs). |
| **Issues** | Read & write | Surface issue/PR bodies and metadata to agents, and post comments on their behalf. Comments on both issues and PR conversations go through the Issues API — there is **no** separate `issue_comment` permission. |
| **Pull requests** | Read-only | Surface PR diffs and metadata to agents. |
| **Metadata** | Read-only | Mandatory for every GitHub App. GitHub auto-selects this — leave it. |
| **Commit statuses** (`statuses`) | Read & write | Set commit statuses on agent-driven runs. |
| **Checks** (`checks`) | Read & write | Open check runs on agent-driven runs. |

Leave **Organization permissions** and **Account permissions** unchanged (none granted).

> If you intend to use the connector's file-write skills (CreateBranch, WriteFile, MergePullRequest), bump **Contents** and **Pull requests** to **Read & write**. The minimal scope set above is sufficient for read-only / commenting workflows; the write scopes are opt-in because they widen the App's blast radius.

### 4. Subscribe to webhook events

Tick exactly these events under **Subscribe to events**:

- `Issues`
- `Pull request`
- `Issue comment`

Do **not** tick `Installation` — GitHub delivers installation / uninstallation events to every App automatically, so it is not a subscribable event (and the App-from-manifest flow rejects it if listed). The connector still learns about installs without subscribing. The three events above drive the agent activity bus; leave every other event unticked.

### 5. Create the App and capture credentials

Click **Create GitHub App**. GitHub opens the App's settings page. From here:

1. Note the **App ID** (numeric, near the top — e.g. `123456`).
2. Note the **Public link** at the top of the page. The URL is `https://github.com/apps/<slug>` — `<slug>` is what goes in `GitHub__AppSlug`.
3. Scroll to **Private keys** and click **Generate a private key**. GitHub downloads a `.pem` file. **Keep the file** — you cannot re-download it later, only generate a new one.

Then install the App on at least one repository / organisation:

1. On the App's settings page, click **Install App** in the left sidebar.
2. Pick the account / org and the repositories you want the connector to act on. You can re-scope this list later from the same screen.

> **App-installation scope is the SV-side subscription model.** Per [ADR-0045](../../decisions/0045-connector-domain-agnostic-platform.md), the platform does not create per-repo hooks on github.com — it relies on the App's own delivery channel. **Which repos the App is installed on determines what events SV receives.** SV silently drops deliveries from repos no unit is bound to (logged at `Information` so operators can correlate noise); the wasted-work trade-off is fine for typical OSS deployments and is minimised by installing the App on only the repos that have units bound to them. To scope SV down later, remove the unwanted repos from the App's installation on github.com — there is no SV-side action required.

### 6. Populate `eng/config/spring.env`

Open `eng/config/spring.env` and uncomment the GitHub block. Paste the four values you collected:

```ini
# Numeric — leave UNQUOTED.
GitHub__AppId=123456

# The slug from https://github.com/apps/<slug>.
GitHub__AppSlug=spring-voyage-acme

# The PEM contents — single-quoted, single-line, with literal `\n` between blocks.
# Convert the downloaded .pem file with:
#   awk 'BEGIN{ORS="\\n"}{print}' < downloaded-key.pem
GitHub__PrivateKeyPem='-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIB...\n-----END RSA PRIVATE KEY-----'

# The webhook secret you generated in step 2.
GitHub__WebhookSecret=<the value from step 2>
```

Restart the platform so the connector reloads:

```bash
./deploy.sh restart                                          # Podman (run from eng/deploy/)
docker compose --env-file ../config/spring.env up -d         # Compose (run from eng/deploy/)
```

The single-quoted, single-line PEM convention round-trips through `bash` + `envsubst` + Podman / Docker `--env-file`. The connector decodes literal `\n` back to real newlines before parsing. See [Deployment guide § Tier-1 platform credentials](deployment.md#tier-1-platform-credentials--github-app-identity-env-only) for the full env-file quirks (`#1186`).

`GitHub__PrivateKeyPem` also accepts an absolute container-visible path to a `.pem` file. `~` is **not** expanded by `--env-file`, so mount the file at a known absolute path if you go this route.

## Required values

Whichever path you took, the deployment ends up with these four values populated in `eng/config/spring.env` (or in the platform secret store when you used `--write-secrets`):

| Env var | Source | Notes |
|---------|--------|-------|
| `GitHub__AppId` | App settings page (numeric, top of the page) | Always **unquoted**. Quoting it silently binds as `0`. |
| `GitHub__AppSlug` | App's public URL (`https://github.com/apps/<slug>`) | Required for the wizard's "Install GitHub App" link. Without it, `GET /api/v1/connectors/github/actions/install-url` returns 502. |
| `GitHub__PrivateKeyPem` | The `.pem` file you downloaded under **Private keys** | Either the inlined PEM (single line, `\n` between blocks) or an absolute path to a readable `.pem`. |
| `GitHub__WebhookSecret` | The value you set under **Webhook secret** | Must match what GitHub holds — the connector verifies every incoming delivery's signature. |

## Local-dev recipe

GitHub Apps require **publicly-reachable** webhook URLs — `localhost` will not receive deliveries. The recommended local-dev path is `gh webhook forward`: the operator's already-authenticated `gh` CLI streams deliveries from GitHub and replays them against the local API. No third-party tunnel, no second hostname, no inbound port on the laptop.

### Recommended: `gh webhook forward` (via the convenience script)

**Preconditions**

- `gh` installed and `gh auth login` completed for the GitHub account that owns the repo you want to forward from.
- **Repository admin** permission on the repo. `gh webhook forward` registers a short-lived forwarding hook through GitHub's webhook-forwarding API, which requires admin on the target repository.
- The `gh-webhook` extension installed:

  ```bash
  gh extension install cli/gh-webhook
  ```

- `eng/config/spring.env` populated (so `GitHub__WebhookSecret` is present — the script reads it so the signatures `gh` adds to forwarded payloads match what the API verifies).

**Run it**

```bash
# In one terminal: start the local platform so the API listens on :8080.
./eng/deploy/deploy.sh up        # Podman (source tree)
# (or, from eng/deploy/, `docker compose --env-file ../config/spring.env up -d` for Compose,
#  or `dotnet run --project src/Cvoya.Spring.Host.Api` for a source-tree run.)

# In another terminal: forward webhooks from your dev repo to the local API.
voyage gh-webhook-forward --repo your-org/your-dev-repo            # installer-based deployments
./eng/deploy/gh-webhook-forward.sh --repo your-org/your-dev-repo   # source-tree development
```

Both paths invoke the same underlying script — `voyage gh-webhook-forward` is a thin wrapper that finds the bundled copy and points `--env` at this install's `spring.env` so you don't have to. The forwarder defaults the destination URL to `http://localhost:8080/api/v1/webhooks/github` (override with `--url`) and reads `GitHub__WebhookSecret` from `spring.env` (override with `--env`). See `voyage gh-webhook-forward --help` (or `./eng/deploy/gh-webhook-forward.sh --help`) for the full flag list, and [`eng/deploy/README.md`](../../../eng/deploy/README.md#local-dev-webhook-forwarding-gh-webhook-forwardsh) for the corresponding operator-reference entry.

Stop with Ctrl-C; the forwarding hook GitHub registers is short-lived and tears down automatically when the `gh` process exits.

> The App's **Webhook URL** field on github.com can stay set to your production-facing URL (or a placeholder). `gh webhook forward` does not change the App's configured URL — it subscribes to a parallel forwarding channel for the duration of the command.

### Without `gh auth` — bundled SSH relay

If you cannot use `gh auth login` (no GitHub account on the dev host, blocked by policy, etc.), use the bundled SSH reverse tunnel: `eng/deploy/relay.sh` exposes the local API through a small VPS you already control. See [`eng/deploy/README.md § Local-dev webhook tunnel (relay.sh)`](../../../eng/deploy/README.md#local-dev-webhook-tunnel-relaysh) for the configuration shape.

### Alternatives (third-party tunnels)

[smee.io](https://smee.io/), [cloudflared](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/), and [ngrok](https://ngrok.com/) all work — pick one, point it at `http://localhost:8080/api/v1/webhooks/github`, and set the resulting public URL as the App's **Webhook URL**. They are kept here for parity with existing operator workflows, but `gh webhook forward` is preferred: it reuses the same `gh` credentials you already have, avoids a third-party hop, and does not require changing the App's configured webhook URL.

### Use a separate "dev" App

Whichever option you pick, **register a separate "dev" App** pointed at `http://localhost:*` (or your tunnel URL) and use a different `GitHub__AppId` in the local `spring.env` than the one you use in production. Sharing one App across dev and prod cross-contaminates webhook deliveries and rate limits.

## Verifying the install

After restarting the platform:

```bash
# 1. Connector reports as enabled (HTTP 200, not the disabled-with-reason 404).
curl -fsS http://localhost/api/v1/connectors/github/actions/list-installations

# 2. Install URL renders the App's public install page.
curl -fsS http://localhost/api/v1/connectors/github/actions/install-url

# 3. Send a test webhook delivery from the App's settings page
#    (Advanced → Recent Deliveries → Redeliver). Tail the API logs:
(cd eng/deploy && docker compose --env-file ../config/spring.env logs -f spring-api) | grep -i webhook
```

A `204` response from the webhook ingress endpoint and a green "Last delivery was successful" badge in the GitHub UI confirms the round-trip.

If `list-installations` returns a structured `404` with a "GitHub App not configured" reason, the credentials did not bind. The most common causes are:

- `GitHub__AppId` is quoted (it must be unquoted — see [Deployment guide § Tier-1 platform credentials](deployment.md#tier-1-platform-credentials--github-app-identity-env-only)).
- `GitHub__PrivateKeyPem` is a path that does not resolve to a valid PEM inside the container.
- The platform was not restarted after the env file changed.

See [Architecture — Connectors § disabled-with-reason](../../architecture/connectors.md#disabled-with-reason-pattern) for the validation model.

## Related documentation

- [Deployment guide § Tier-1 platform credentials](deployment.md#tier-1-platform-credentials--github-app-identity-env-only) — env-file shape and quirks.
- [Managing Secrets § The three config tiers](secrets.md#the-three-config-tiers-615) — why GitHub App credentials are tier-1 (deployment identity) and not tier-2.
- [GitHub Connector README](../../../src/Cvoya.Spring.Connector.GitHub/README.md) — the per-setting configuration table the connector binds.
- [Architecture — Connectors](../../architecture/connectors.md) — the `IConnectorType` contract and the GitHub connector's disabled-with-reason model.
- GitHub docs — [Registering a GitHub App](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/registering-a-github-app), [Generating a private key](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps).
