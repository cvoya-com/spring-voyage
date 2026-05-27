# Register your Slack app

Spring Voyage's Slack connector authenticates as a **Slack app that the operator owns and registers themselves**. Spring Voyage does **not** ship a shared Slack-app client ID or signing secret, and there is no central `api.spring-voyage.com` callback that brokers installs through us. Each deployment registers its own Slack app, and the redirect URL, event-subscription URL, slash-command URLs, signing secret, and OAuth client credentials all belong to that deployment.

This mirrors the [GitHub App setup](github-app-setup.md) model and the same trade-offs apply:

- A shared client secret would have to ship with our binary or be fetched from a Spring-Voyage-hosted service. Either is a security non-starter for a self-hostable platform.
- Per-deployment apps mean per-deployment delivery rate-limit budgets, audit history, and branding (the bot's display name shown to users in their DM sidebar is the operator's choice).
- A leaked signing secret in one deployment cannot affect any other deployment.
- The redirect URL, event URL, and slash-command URLs all point at the operator's deployment — no fragile redirect dance through a Spring-Voyage-controlled domain.

Per [ADR-0061](../../decisions/0061-slack-connector-oss-shape.md) §1, the Slack binding is **tenant-scoped**: one Slack workspace per SV tenant, one bot identity per binding. OSS v0.1 is **single-bound-user, DM-only** (§§ 2.1, 2.2): the bound user is the OAuth installer, the bot operates only in its DM with that user, and the bot auto-leaves any channel it is invited to. **Slack Enterprise Grid installs are refused at install time** (§ 2.3) — register the app against a standard workspace.

## Document map

- [Path A — `spring connector slack install` (recommended)](#path-a--spring-connector-slack-install-recommended) — one CLI verb that builds the manifest, drives Slack's [App Manifest API](https://api.slack.com/reference/manifests), captures the Client ID / Client Secret / Signing Secret, and writes them straight to `eng/config/spring.env` (or to platform secrets). Replaces ~15 minutes of clicking through api.slack.com with a single command.
- [Path B — Create from manifest (web UI)](#path-b--create-from-manifest-web-ui) — paste a YAML blob into Slack's "Create an app from a manifest" form. Useful when you can't run the CLI on the same host as `eng/config/spring.env`, or you want to inspect the manifest before submitting it.
- [Path C — Manual registration](#path-c--manual-registration) — point-and-click on api.slack.com if you want to inspect every field Slack asks for.
- [Required values](#required-values) — the four `Slack__OAuth__*` env vars every deployment ends up with, regardless of which path you took.
- [Local-dev recipe](#local-dev-recipe) — register a separate "dev" app pointed at a publicly-reachable tunnel.
- [Verifying the install](#verifying-the-install) — confirm the connector picks up the credentials and the OAuth callback completes.

## Prerequisites

- A **standard Slack workspace** (not part of an Enterprise Grid) where you have **workspace owner** or **workspace admin** rights, or "Manage apps" permission. The OAuth installer becomes the deployment's single bound user, so register the app from the same workspace + Slack account that will use the bot.
- Your deployment's **public hostname** as `<your-host>` (the FQDN you set as `DEPLOY_HOSTNAME` in `eng/config/spring.env`). The redirect URL, event URL, and slash-command URLs are derived from it and must be publicly reachable over HTTPS for Slack's signed callbacks to land. For local-dev, see the [Local-dev recipe](#local-dev-recipe) below.
- Access to `eng/config/spring.env` on the deployment host (you will paste the Slack OAuth credentials into the `Slack__OAuth__*` keys).

## Path A — `spring connector slack install` (recommended)

If you have the `spring` CLI on the same host as `eng/config/spring.env`, this is the shortest path:

```bash
cd /path/to/spring-voyage
SV_SLACK_CONFIG_TOKEN=xoxe.xoxp-1-… \
  spring connector slack install \
    --app-name "Spring Voyage (<your-deployment>)" \
    --sv-host "https://<your-host>" \
    --write-env
```

The verb:

1. Builds a manifest scoped to **your** deployment URLs (redirect, events, interactions, slash-commands), embedding the exact bot-scope set from ADR-0061 § 6.
2. POSTs the manifest to Slack's `apps.manifest.validate` endpoint to surface scope / URL typos before any state is created.
3. POSTs it to `apps.manifest.create`, which creates the app inside your workspace and returns `app_id`, `client_id`, `client_secret`, `signing_secret`, and `verification_token` in one shot.
4. Writes `Slack__OAuth__ClientId`, `Slack__OAuth__ClientSecret`, `Slack__OAuth__SigningSecret`, and `Slack__OAuth__RedirectUri` to `eng/config/spring.env`. Pass `--write-secrets` to persist the same values as platform-scoped secrets via the registry instead — that path is atomic, rolling back every secret already written on any subsequent failure.
5. Prints the OAuth install URL the operator's browser visits next.

Restart the platform after the file changes (`./deploy.sh restart` for Podman, `docker compose --env-file ../config/spring.env up -d` from `eng/deploy/` for Compose) so the connector picks up the new credentials. Then drive the install from the SV portal — see [step 4 of Path B](#4-after-populating-springenv-install-the-app-to-your-workspace) for the portal-side flow (the same one applies regardless of which path created the app).

### Generating the Configuration Token

Slack's Manifest API authenticates with a **Configuration Token** generated by a workspace admin:

1. Sign in to your Slack workspace.
2. Visit <https://api.slack.com/apps> → **Your Apps**.
3. Click **Generate Configuration Tokens**.
4. Pick the workspace you want the new app to live in, then **Generate**. Slack returns a `xoxe.xoxp-…` token + a refresh token.
5. Copy the access token. The token is valid for ~12 hours; you only need it for the duration of the `install` command.

Pass it via `--config-token` or the `SV_SLACK_CONFIG_TOKEN` environment variable. Run `spring connector slack install --help` for the full flag list, including `--write-env`, `--write-secrets`, `--env-path`, and `--dry-run` (prints the manifest JSON without contacting Slack — useful for CI / air-gapped review).

## Path B — Create from manifest (web UI)

Slack's [manifest API](https://api.slack.com/reference/manifests) lets you create an app from a single YAML document. Use this path when you cannot run the CLI on the host (air-gapped, or running the CLI elsewhere), or you want to review the manifest before submitting it. Paste the blob below into Slack's "Create an app from a manifest" form, click through one confirmation screen, and capture the three credentials.

### 1. Open Slack's "Create an app" page

1. Go to <https://api.slack.com/apps>.
2. Click **Create New App**.
3. Pick **From a manifest**.
4. Choose the workspace you want the app to live in.

### 2. Paste the manifest

Switch the form to the **YAML** tab and paste the blob below. Before submitting, replace every `<your-host>` placeholder with your deployment's public hostname.

```yaml
display_information:
  name: Spring Voyage
  description: Spring Voyage — agent platform integration for this Slack workspace.
  background_color: "#0a0a0a"
features:
  bot_user:
    display_name: Spring Voyage
    always_online: true
  slash_commands:
    - command: /sv-thread
      url: https://<your-host>/api/v1/tenant/connectors/slack/commands
      description: Start a new SV thread with one or more agents, units, or humans
      usage_hint: "(opens a participant picker)"
      should_escape: false
    - command: /sv-threads
      url: https://<your-host>/api/v1/tenant/connectors/slack/commands
      description: List your active SV threads in this workspace
      should_escape: false
    - command: /sv-help
      url: https://<your-host>/api/v1/tenant/connectors/slack/commands
      description: Show the Spring Voyage Slack cheat sheet
      should_escape: false
oauth_config:
  redirect_urls:
    - https://<your-host>/api/v1/tenant/connectors/slack/oauth/callback
  scopes:
    bot:
      - chat:write
      - chat:write.customize
      - im:history
      - im:write
      - im:read
      - users:read
      - users:read.email
      - commands
      - channels:read
      - groups:read
settings:
  event_subscriptions:
    request_url: https://<your-host>/api/v1/tenant/connectors/slack/events
    bot_events:
      - message.im
      - member_joined_channel
  interactivity:
    is_enabled: true
    request_url: https://<your-host>/api/v1/tenant/connectors/slack/interactions
  org_deploy_enabled: false
  socket_mode_enabled: false
  token_rotation_enabled: false
```

### 3. Confirm and create

Slack renders a summary of every scope, URL, and command. **Sanity-check that every URL points at your deployment** — if you forgot to replace `<your-host>` Slack will accept the literal placeholder and the OAuth handshake will fail later. Click **Create**.

Slack opens the new app's settings page. From here, capture the three credentials:

1. **Basic Information → App Credentials → Client ID** (visible field).
2. **Basic Information → App Credentials → Client Secret** — click **Show** and copy.
3. **Basic Information → App Credentials → Signing Secret** — click **Show** and copy.

Skip directly to [Required values](#required-values) to paste these into `eng/config/spring.env`.

### 4. (After populating `spring.env`) Install the app to your workspace

The OSS v0.1 connector does not use Slack's "Install to Workspace" button on the app settings page. Instead, the install flow is driven by the SV portal:

1. After populating `Slack__OAuth__*` in `eng/config/spring.env` and restarting the platform (see [Required values](#required-values)), open the portal at `https://<your-host>/connectors/slack`.
2. Click **Connect Slack**. The portal opens a popup pointed at `https://slack.com/oauth/v2/authorize?...` with your app's client ID and the scopes from the manifest.
3. Approve the install in your workspace. Slack redirects to the deployment's `/api/v1/tenant/connectors/slack/oauth/callback`, which exchanges the code, persists the bot token + signing secret as tenant secrets, and posts the result back to the portal popup.

The OAuth installer becomes the deployment's single bound user.

## Path C — Manual registration

Use this path when you want to review every field Slack asks for, or you cannot use the manifest form for any reason.

### 1. Create a new app

1. Go to <https://api.slack.com/apps>.
2. Click **Create New App → From scratch**.
3. Name it `Spring Voyage` (or whatever makes sense for your deployment) and pick the workspace.

### 2. Add a bot user

Under **Features → App Home**:

- Tick **Always Show My Bot as Online**.
- Set the bot's display name to `Spring Voyage` (or your preferred branding — this is what appears in users' DM sidebars and in `chat.postMessage` posts when the persona override is not applied).

### 3. Configure OAuth + redirect URL

Under **OAuth & Permissions → Redirect URLs**:

- Add `https://<your-host>/api/v1/tenant/connectors/slack/oauth/callback`.
- Click **Save URLs**.

Under **OAuth & Permissions → Scopes → Bot Token Scopes**, add exactly these ten scopes (no more — every extra scope widens the blast radius if the signing secret leaks):

| Scope | Why |
|-------|-----|
| `chat:write` | Bot posts in DMs (ADR-0061 § 3, § 6). |
| `chat:write.customize` | Persona overrides (`username`, `icon_url`) for SV participants other than the bound user (ADR-0061 § 3). |
| `im:history` | Read DM messages from the bound user (ADR-0061 § 2.2). |
| `im:write` | Open / write DMs. |
| `im:read` | DM metadata (member list confirmation). |
| `users:read` | Resolve `user_id` → display name for the binding. |
| `users:read.email` | Map the OAuth installer to an SV `TenantUser` by email (optional; pasteable fallback). |
| `commands` | Slash commands (`/sv-thread`, `/sv-threads`, `/sv-help`). |
| `channels:read` | Receive `member_joined_channel` so the bot can auto-leave public channels (ADR-0061 § 2.2). |
| `groups:read` | Same, for private channels. |

Do **not** add `channels:history`, `groups:history`, `mpim:*`, `app_mentions:read`, or any `team:*` scope. Per ADR-0061 § 6 they are intentionally excluded from v0.1 — adding them widens the bot's reach beyond DM operation without enabling any feature the connector exercises.

### 4. Configure event subscriptions

Under **Features → Event Subscriptions**:

- Toggle **Enable Events** on.
- Set **Request URL** to `https://<your-host>/api/v1/tenant/connectors/slack/events`. Slack issues a one-time `url_verification` challenge against this URL; the connector signs every response with the workspace's signing secret, so the URL only goes green **after** you populate `Slack__OAuth__SigningSecret` in `spring.env` and restart the platform. If you are configuring the app for the first time, expect the green checkmark to appear after you complete [Required values](#required-values) and re-save this page.
- Under **Subscribe to bot events**, add exactly:
  - `message.im` — DM messages from the bound user (drives inbound routing per ADR-0061 § 3).
  - `member_joined_channel` — fires when the bot is invited to any channel, so the connector can post the leave message and call `conversations.leave` per ADR-0061 § 2.2.

Leave every other event unticked.

### 5. Configure interactivity

Under **Features → Interactivity & Shortcuts**:

- Toggle **Interactivity** on.
- Set **Request URL** to `https://<your-host>/api/v1/tenant/connectors/slack/interactions`. This is the endpoint Slack hits when the user submits the `/sv-thread` modal (ADR-0061 § 5).

### 6. Configure slash commands

Under **Features → Slash Commands**, create three commands. All three point at the **same** request URL — the dispatcher selects by the slash-command slug.

| Command | Request URL | Short Description | Usage Hint |
|---------|-------------|-------------------|------------|
| `/sv-thread` | `https://<your-host>/api/v1/tenant/connectors/slack/commands` | Start a new SV thread with one or more agents, units, or humans | `(opens a participant picker)` |
| `/sv-threads` | `https://<your-host>/api/v1/tenant/connectors/slack/commands` | List your active SV threads in this workspace | _(blank)_ |
| `/sv-help` | `https://<your-host>/api/v1/tenant/connectors/slack/commands` | Show the Spring Voyage Slack cheat sheet | _(blank)_ |

Leave **Escape channels, users, and links sent to your app** unticked for all three.

### 7. Capture credentials

Under **Settings → Basic Information → App Credentials**, capture:

1. **Client ID** (visible field).
2. **Client Secret** (click **Show**).
3. **Signing Secret** (click **Show**).

Continue to [Required values](#required-values).

## Required values

Whichever path you took, the deployment ends up with these four values populated in `eng/config/spring.env` (or in the platform secret store when you used `--write-secrets`):

| Env var | Source | Notes |
|---------|--------|-------|
| `Slack__OAuth__ClientId` | App settings → **Basic Information → App Credentials → Client ID** | Public — surfaces in the Slack consent URL. |
| `Slack__OAuth__ClientSecret` | App settings → **Basic Information → App Credentials → Client Secret** | Server-side only — required at OAuth callback time for the `oauth.v2.access` exchange. |
| `Slack__OAuth__SigningSecret` | App settings → **Basic Information → App Credentials → Signing Secret** | Slack signs every event, slash-command, and interaction delivery with this secret. The connector verifies `X-Slack-Signature` on every inbound request and rejects mismatches with 401. The OAuth callback persists this value as a per-tenant secret alongside the bot token. |
| `Slack__OAuth__RedirectUri` | The URL you registered under **OAuth & Permissions → Redirect URLs** | Must exactly match what Slack holds, character-for-character. Slack rejects the OAuth code exchange if the redirect URI sent at callback time differs from the one on the app. |

Path A populates these automatically. For Path B and Path C, open `eng/config/spring.env` manually and add the Slack block:

```ini
Slack__OAuth__ClientId=1234567890.1234567890123
Slack__OAuth__ClientSecret=<the value from Basic Information>
Slack__OAuth__SigningSecret=<the value from Basic Information>
Slack__OAuth__RedirectUri=https://<your-host>/api/v1/tenant/connectors/slack/oauth/callback
```

Restart the platform so the connector reloads:

```bash
./deploy.sh restart                                          # Podman (run from eng/deploy/)
docker compose --env-file ../config/spring.env up -d         # Compose (run from eng/deploy/)
```

Then drive the install from the SV portal:

1. Open `https://<your-host>/connectors/slack`.
2. Click **Connect Slack** and approve the install in your workspace.
3. The portal popup reports `Slack workspace '<team_id>' connected.` on success.

The OAuth installer becomes the deployment's single bound user. Re-binding to a different workspace is supported — disconnect the existing binding from the portal, then run the OAuth flow again from the new workspace. The OAuth callback refuses Enterprise Grid installs with a structured `SlackEnterpriseGridUnsupported` error (ADR-0061 § 2.3).

## Local-dev recipe

Slack apps require **publicly-reachable** event, interaction, and slash-command URLs — Slack signs each delivery from its own infrastructure, so `localhost` will not receive deliveries. The standard local-dev path is a third-party tunnel.

### 1. Stand up a tunnel

Pick one — any tool that exposes a stable HTTPS URL pointed at `http://localhost:8080` works:

- [ngrok](https://ngrok.com/) — `ngrok http 8080` prints a `https://<random>.ngrok.app` URL.
- [Cloudflare Tunnel (`cloudflared`)](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/) — gives you a stable subdomain on a domain you control.
- The bundled SSH reverse-tunnel (`eng/deploy/relay.sh`) — see [`eng/deploy/README.md § Local-dev webhook tunnel (relay.sh)`](../../../eng/deploy/README.md#local-dev-webhook-tunnel-relaysh).

Whichever option you pick, capture the resulting public hostname (call it `<tunnel-host>`) and keep the tunnel running for the rest of the dev session.

### 2. Register a separate "dev" app

**Register a fresh Slack app** specifically for local-dev — do not share one Slack app between dev and production. Sharing one app means dev and prod compete for the same bot OAuth token and event-delivery channel, and any OAuth re-install from one environment kicks the other off the bot identity.

Follow any path — [Path A](#path-a--spring-connector-slack-install-recommended) (CLI; pass `--sv-host https://<tunnel-host>`), [Path B](#path-b--create-from-manifest-web-ui) (paste YAML; substitute `<tunnel-host>` for `<your-host>` in the blob), or [Path C](#path-c--manual-registration) (manual; set the tunnel hostname in every URL field).

### 3. Populate the dev `spring.env`

Use a different copy of `eng/config/spring.env` than the one your production deployment reads — the local CLI usually points at `~/.spring-voyage/config/spring.env` or similar. Paste the dev app's Client ID, Client Secret, and Signing Secret in. Set `Slack__OAuth__RedirectUri` to the dev app's registered redirect URL.

### 4. Start the platform behind the tunnel

```bash
./eng/deploy/deploy.sh up        # Podman (source tree)
# or, from eng/deploy/, `docker compose --env-file ../config/spring.env up -d` for Compose,
# or `dotnet run --project src/Cvoya.Spring.Host.Api` for a source-tree run.
```

Confirm the tunnel forwards `https://<tunnel-host>/api/v1/tenant/connectors/slack/healthz` to your local API (the connector exposes that endpoint for liveness probes; it should return `{ slug: "slack", registered: true, bindingScope: "Tenant" }`). Then open Slack's app settings page for the dev app, re-save **Event Subscriptions → Request URL**, and confirm Slack reports the URL as **Verified** — that handshake exercises both the tunnel and the signing-secret round-trip.

Drive the install from the dev portal at `https://<tunnel-host>/connectors/slack`. Stop the tunnel and the platform with Ctrl-C when done.

## Verifying the install

After populating the env vars and restarting the platform:

```bash
# 1. Connector reports as registered.
curl -fsS https://<your-host>/api/v1/tenant/connectors/slack/healthz
# → { "slug": "slack", "registered": true, "bindingScope": "Tenant" }

# 2. Slack's signature on a forged events request is rejected.
curl -sS -o /dev/null -w "%{http_code}\n" \
  -H 'Content-Type: application/json' \
  -H 'X-Slack-Request-Timestamp: 0' \
  -H 'X-Slack-Signature: v0=deadbeef' \
  --data '{"type":"url_verification","challenge":"x"}' \
  https://<your-host>/api/v1/tenant/connectors/slack/events
# → 401

# 3. Slack's own URL-verification round-trip succeeds:
#    Re-save Event Subscriptions → Request URL on api.slack.com; the
#    field reports "Verified" once the signed challenge round-trips.
```

After clicking **Connect Slack** in the portal, the OSS deployment ends up with one binding row. Verify it:

```bash
# 4. The bot has opened its DM with the installer and posted the install greeting.
#    Look in your Slack workspace under "Direct messages" → the app's bot user.

# 5. Tail the API logs for confirmation.
(cd eng/deploy && docker compose --env-file ../config/spring.env logs -f spring-api) | grep -i slack
# Look for: "Slack binding persisted (team_id=…, bot_user_id=…, installer=…)"
```

Common failures and where to look:

- **Slack's "Verified" badge stays red on Event Subscriptions.** Either `Slack__OAuth__SigningSecret` is missing / wrong, the platform was not restarted after the env file changed, or the request URL on api.slack.com does not match what your deployment actually serves. Re-save the request URL after restart.
- **OAuth popup closes with `SlackEnterpriseGridUnsupported`.** The workspace you are installing into is part of a Slack Enterprise Grid. Per ADR-0061 § 2.3 the v0.1 connector refuses Grid installs. Register the app against a standard (non-Grid) workspace.
- **OAuth popup closes with `SlackWorkspaceConflict`.** This tenant is already bound to a different workspace. Disconnect the existing binding from `https://<your-host>/connectors/slack` first, then re-run the install.
- **OAuth popup closes with `oauth_not_configured`.** One of the `Slack__OAuth__*` env vars is missing. Check the platform logs for the exact `Slack:OAuth:<Key> is not configured` message.
- **Slash commands return "dispatch_failed"** in Slack. Either the platform is not reachable at the registered slash-command URL, the signature verification is failing (signing-secret mismatch), or the command is being invoked outside the bot DM — Slack's reply is the ephemeral DM-only refusal text per ADR-0061 § 5.

## Related documentation

- [Deployment guide](deployment.md) — env-file shape and quirks (the Slack block lives alongside the GitHub block).
- [Managing Secrets](secrets.md) — tier model for OAuth credentials and the per-tenant bot-token / signing-secret rows the OAuth callback writes.
- [Connectors operator guide](connectors.md) — installing, inspecting, and uninstalling connectors per tenant.
- [Architecture — Connectors](../../architecture/connectors.md) — `IConnectorType`, tenant-scoped bindings, and the Slack connector's place in the contract.
- [ADR-0061 — Slack connector v0.1 OSS shape](../../decisions/0061-slack-connector-oss-shape.md) — the OSS restrictions, OAuth scopes, and forward-compat seams this doc operationalises.
- Slack docs — [Creating an app from a manifest](https://api.slack.com/reference/manifests), [Verifying requests from Slack](https://api.slack.com/authentication/verifying-requests-from-slack), [OAuth v2 flow](https://api.slack.com/authentication/oauth-v2).
