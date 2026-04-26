# Register your GitHub App

Spring Voyage's GitHub connector authenticates as a **GitHub App that the operator owns and registers themselves**. Spring Voyage does **not** ship a shared App private key, and there is no central `api.spring-voyage.com` callback that brokers installs through us. Each deployment registers its own App, and the App's webhook URL, callback URL, App ID, slug, and private key all belong to that deployment.

This is the same model Renovate, Sentry self-hosted, Linear self-hosted, and Probot apps use. The trade-offs are worth saying out loud:

- A shared private key would have to ship with our binary or be fetched from a Spring-Voyage-hosted service. Either is a security non-starter for a self-hostable platform.
- Per-deployment Apps mean per-deployment webhook deliveries, rate-limit budgets, audit logs, and branding (the App name shown to repo owners is the operator's choice).
- A leaked key in one deployment cannot affect any other deployment.
- The webhook URL, callback URL, and setup URL are all owned by the operator — no fragile redirect dance through a Spring-Voyage-controlled domain.

This page is the operator-facing companion to [Deployment guide § Tier-1 platform credentials](deployment.md#tier-1-platform-credentials--github-app-identity-env-only). Pick **one** of the two paths below; both produce the same set of values in `deployment/spring.env`.

## Document map

- [Path A — `spring github-app register` (recommended)](#path-a--spring-github-app-register-recommended) — one CLI verb that drives GitHub's [App-from-manifest flow](https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest), opens the pre-filled "create App" page, captures the conversion code on a loopback listener, and writes the env file for you.
- [Path B — Manual registration](#path-b--manual-registration) — point-and-click in `github.com` if you want to inspect every field or you cannot run the CLI on the host that owns `deployment/spring.env`.
- [Required values](#required-values) — the shape of every field, regardless of which path you took.
- [Local-dev recipe](#local-dev-recipe) — register a separate "dev" App pointed at `http://localhost:*` URLs.
- [Verifying the install](#verifying-the-install) — confirm the connector picks up the credentials and the App can mint installation tokens.

## Path A — `spring github-app register` (recommended)

If you already have the `spring` CLI on the same host as `deployment/spring.env`, this is the shortest path:

```bash
cd /path/to/spring-voyage
spring github-app register --name "Spring Voyage (<your-deployment>)"
```

The verb:

1. Builds a manifest scoped to **your** deployment URLs (callback, webhook, setup) and base64-encodes it.
2. Binds a loopback HTTP listener on `127.0.0.1:<ephemeral-port>` for the conversion callback.
3. Opens your browser on `https://github.com/settings/apps/new?manifest=…` (or `https://github.com/organizations/<org>/settings/apps/new?...` when you pass `--org <slug>`) — GitHub renders the "create App" confirmation page **pre-filled** with the right name, permissions, events, callback URL, and webhook URL. You click **Create**.
4. GitHub redirects back to the loopback listener with a one-time conversion `code`.
5. The CLI exchanges the code via `POST /app-manifests/{code}/conversions` and receives `app_id`, `slug`, `pem`, `webhook_secret`, `client_id`, and `client_secret` back in the response.
6. The CLI writes `GitHub__AppId`, `GitHub__AppSlug`, `GitHub__PrivateKeyPem` (single-quoted, single-line, with literal `\n` between blocks), and `GitHub__WebhookSecret` to `deployment/spring.env`. Pass `--write-secrets` to persist the same values as platform-scoped secrets via the registry instead of the env file.

Restart the platform after the file changes (`./deploy.sh restart` for Podman, `docker compose --env-file spring.env up -d` for Compose) so the connector picks up the new credentials.

See [`docs/architecture/cli-and-web.md § GitHub App bootstrap verb (#631)`](../../architecture/cli-and-web.md#github-app-bootstrap-verb-631) for the full flag list, including `--org`, `--write-secrets`, and `--public-url`.

## Path B — Manual registration

Use this path when you cannot run the CLI on the host (e.g. an air-gapped operator running the CLI elsewhere), or you want to review every field GitHub asks for.

### 1. Decide where the App lives

GitHub Apps belong either to your personal account or to an organisation:

- **Personal:** `https://github.com/settings/apps/new`
- **Organisation:** `https://github.com/organizations/<org-slug>/settings/apps/new` (you must have the **Owner** role on the org).

Pick the organisation account when more than one person on your team needs to manage the App's settings, rotate its private key, or read its webhook deliveries — App ownership is account-level, not user-level.

### 2. Fill in the App settings

GitHub's "Register new GitHub App" page asks for the following. Substitute your deployment's public hostname (the FQDN you set as `DEPLOY_HOSTNAME` in `deployment/spring.env`) wherever the table says `<your-host>`:

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
| **Issues** | Read-only | Surface issue bodies and metadata to agents. |
| **Pull requests** | Read-only | Surface PR diffs and metadata to agents. |
| **Metadata** | Read-only | Mandatory for every GitHub App. GitHub auto-selects this — leave it. |
| **Issues and PR comments** (`issue_comment`) | Read & write | Post comments authored by agents. |
| **Commit statuses** (`statuses`) | Read & write | Set commit statuses on agent-driven runs. |
| **Checks** (`checks`) | Read & write | Open check runs on agent-driven runs. |

Leave **Organization permissions** and **Account permissions** unchanged (none granted).

> If you intend to use the connector's file-write skills (CreateBranch, WriteFile, MergePullRequest), bump **Contents** and **Pull requests** to **Read & write**. The minimal scope set above is sufficient for read-only / commenting workflows; the write scopes are opt-in because they widen the App's blast radius.

### 4. Subscribe to webhook events

Tick exactly these events under **Subscribe to events**:

- `Issues`
- `Pull request`
- `Issue comment`
- `Installation`

`Installation` is required so the connector learns when the App is installed or uninstalled on a new org or repo. The other three drive the agent activity bus. Leave every other event unticked.

### 5. Create the App and capture credentials

Click **Create GitHub App**. GitHub opens the App's settings page. From here:

1. Note the **App ID** (numeric, near the top — e.g. `123456`).
2. Note the **Public link** at the top of the page. The URL is `https://github.com/apps/<slug>` — `<slug>` is what goes in `GitHub__AppSlug`.
3. Scroll to **Private keys** and click **Generate a private key**. GitHub downloads a `.pem` file. **Keep the file** — you cannot re-download it later, only generate a new one.

Then install the App on at least one repository / organisation:

1. On the App's settings page, click **Install App** in the left sidebar.
2. Pick the account / org and the repositories you want the connector to act on. You can re-scope this list later from the same screen.

### 6. Populate `deployment/spring.env`

Open `deployment/spring.env` and uncomment the GitHub block. Paste the four values you collected:

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
./deploy.sh restart                                # Podman
docker compose --env-file spring.env up -d         # Compose
```

The single-quoted, single-line PEM convention round-trips through `bash` + `envsubst` + Podman / Docker `--env-file`. The connector decodes literal `\n` back to real newlines before parsing. See [Deployment guide § Tier-1 platform credentials](deployment.md#tier-1-platform-credentials--github-app-identity-env-only) for the full env-file quirks (`#1186`).

`GitHub__PrivateKeyPem` also accepts an absolute container-visible path to a `.pem` file. `~` is **not** expanded by `--env-file`, so mount the file at a known absolute path if you go this route.

## Required values

Whichever path you took, the deployment ends up with these four values populated in `deployment/spring.env` (or in the platform secret store when you used `--write-secrets`):

| Env var | Source | Notes |
|---------|--------|-------|
| `GitHub__AppId` | App settings page (numeric, top of the page) | Always **unquoted**. Quoting it silently binds as `0`. |
| `GitHub__AppSlug` | App's public URL (`https://github.com/apps/<slug>`) | Required for the wizard's "Install GitHub App" link. Without it, `GET /api/v1/connectors/github/actions/install-url` returns 502. |
| `GitHub__PrivateKeyPem` | The `.pem` file you downloaded under **Private keys** | Either the inlined PEM (single line, `\n` between blocks) or an absolute path to a readable `.pem`. |
| `GitHub__WebhookSecret` | The value you set under **Webhook secret** | Must match what GitHub holds — the connector verifies every incoming delivery's signature. |

## Local-dev recipe

GitHub Apps require **publicly-reachable** webhook URLs — `localhost` will not receive deliveries. You have three workable options:

1. **Use Path A's loopback handler for OAuth and skip webhooks.** `spring github-app register` works against `http://localhost:5173/...` callback URLs because the conversion callback is loopback-only. The App's webhook URL still has to be publicly reachable for deliveries; configure the App with a placeholder URL and rely on the connector's polling fallback for local-dev experiments.
2. **Tunnel a public URL to your laptop.** Use [smee.io](https://smee.io/), [cloudflared](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/), or [ngrok](https://ngrok.com/) to forward a public hostname to `http://localhost:8080/api/v1/connectors/github/webhooks`. Configure the App's webhook URL with the tunnel URL.
3. **Use the bundled webhook relay.** `deployment/relay.sh` opens an SSH reverse tunnel from a small VPS you already control to the local API host. See [`deployment/README.md § Local-dev webhook tunnel (relay.sh)`](../../../deployment/README.md#local-dev-webhook-tunnel-relaysh) for the configuration shape.

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
docker compose --env-file deployment/spring.env logs -f spring-api | grep -i webhook
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
- [Architecture — CLI & Web § GitHub App bootstrap verb](../../architecture/cli-and-web.md#github-app-bootstrap-verb-631) — `spring github-app register` flag reference.
- GitHub docs — [Registering a GitHub App](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/registering-a-github-app), [Generating a private key](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps).
