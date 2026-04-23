# GitHub Connector

`Cvoya.Spring.Connector.GitHub` binds Spring Voyage units to GitHub
repositories — translating webhook events into platform messages,
exposing GitHub-aware skills (issues, pull requests, comments, branch /
file ops, label routing), and handling the GitHub App auth surface
(installation token minting, OAuth, webhook signature validation).

The connector self-registers as an `IConnectorType` (slug `github`).
The Host.Api project iterates every registered `IConnectorType` at
startup and maps its routes under `/api/v1/connectors/{slug}`, so
nothing GitHub-specific lives in the API host.

## Configuration

Bound from the `GitHub` configuration section
(`GitHubConnectorOptions`):

| Setting | Env var | Required | Purpose |
|---------|---------|----------|---------|
| `AppId` | `GitHub__AppId` | yes (for write paths) | Numeric GitHub App id used as the JWT issuer. |
| `PrivateKeyPem` | `GitHub__PrivateKeyPem` | yes | PEM-encoded App private key. Inlined verbatim, inlined as a single line with literal `\n` between blocks (decoded automatically — same convention as Firebase / GCP service-account keys), or an absolute container-visible path to a `.pem` file. |
| `WebhookSecret` | `GitHub__WebhookSecret` | recommended | Shared secret used to verify incoming webhook signatures. |
| `InstallationId` | `GitHub__InstallationId` | optional | Pin operations to a specific installation; otherwise the connector picks the first installation visible to the App. |
| `AppSlug` | `GitHub__AppSlug` | required for install URL | The App's public slug, used to build `https://github.com/apps/{slug}/installations/new`. |
| `WebhookUrl` | `GitHub__WebhookUrl` | yes (for unit start) | Public URL the connector registers webhooks against on unit start. |

> **Env-file gotchas.** podman / docker `--env-file` keeps surrounding quotes literally and does not support multi-line values. Always write `GitHub__*` values UNQUOTED, and inline the PEM as one line with `\n` separators. See [Deployment guide § Tier-1 platform credentials](../../docs/guide/deployment.md#tier-1-platform-credentials--github-app-identity-env-only) for the full set of pitfalls.

Missing `AppId` / `PrivateKeyPem` keep the connector registered but
disabled — the credential requirement
(`GitHubAppConfigurationRequirement`) reports `Disabled` and the
hot-path endpoints (`actions/list-installations`, `actions/install-url`)
short-circuit with a structured 404 instead of failing on a JWT sign.

## Credential validation

The connector implements the optional `IConnectorType.ValidateCredentialAsync`
hook from the `Cvoya.Spring.Connectors` abstractions (#685, part of
the phase-2 refactor of #674). Callers — the credential-health store,
the wizard, the system-configuration endpoint — invoke the hook
through `IConnectorType` without importing GitHub-specific code.

**What the hook does.**

1. Consults `GitHubAppConfigurationRequirement`. If the credentials are
   absent or malformed at startup, the hook returns
   `CredentialValidationResult { Status = Unknown }` with the disabled
   reason so the credential-health store treats it as "pending /
   nothing to check yet" rather than "broken".
2. Picks an installation id. The configured
   `GitHubConnectorOptions.InstallationId` wins; otherwise the hook
   falls back to the first installation surfaced by `GET /app/installations`
   (signed with the App JWT).
3. Mints an installation access token via
   `GET /app/installations/{id}/access_tokens` and exchanges it for a
   call to `GET /installation/repositories` — the canonical "is this
   installation token actually accepted" probe.
4. Maps the outcome to a `CredentialValidationResult`:

   | Outcome | `Status` |
   |---------|----------|
   | Both calls succeed | `Valid` |
   | App configured but reports zero installations | `Valid` (the App JWT itself was accepted) |
   | 401 / 403 from either call (`AuthorizationException` or `ApiException`) | `Invalid` |
   | DNS / TLS / connection / 5xx / timeout | `NetworkError` |

The `credential` parameter is currently ignored — GitHub App auth is
multi-part (App id + private key + installation id), so the hook
validates the connector's own bound configuration rather than a
single token. The signature is shared across `IConnectorType`
implementations, so connectors that DO accept a single token can use
it directly.

**What the hook does not do.**

- Persist credential health. The store contract lands in a follow-up
  phase-2 sub-issue.
- Flip credential health on a 401/403 from a hot-path call. That's a
  separate sub-issue on the middleware side.
- Rotate or refresh App credentials. Operators manage `GITHUB_APP_*`
  values; the connector consumes them.

## Container baseline

`IConnectorType.VerifyContainerBaselineAsync` is also implemented and
returns `Passed=true` with no errors — the GitHub connector talks to
`api.github.com` over outbound HTTPS only and has no host-side binary
or sidecar to verify. We return a passing result rather than `null` so
the install / wizard surface renders "checked, OK" instead of
"skipped" for the connector most operators care about.

## Endpoints (recap)

The connector owns the route group at
`/api/v1/connectors/github/...` and maps:

- `GET units/{unitId}/config` — read the bound per-unit config.
- `PUT units/{unitId}/config` — bind a unit and upsert its config.
- `GET actions/list-installations` — installations the App can see.
- `GET actions/install-url` — public install URL for the App.
- `GET config-schema` — JSON Schema describing the per-unit config.
- OAuth authorize / callback / revoke / session endpoints (see
  `Auth/OAuth/`).
