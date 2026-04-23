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
| `AppSlug` | `GitHub__AppSlug` | required for install URL | The App's public slug as it appears in the App's URL on github.com (`https://github.com/apps/<slug>`). The operator picks the App name when registering — see [Register your GitHub App](../../docs/guide/github-app-setup.md) — and GitHub derives the slug from the chosen name. The connector uses it to build `https://github.com/apps/{slug}/installations/new`. |
| `WebhookUrl` | `GitHub__WebhookUrl` | yes (for unit start) | Public URL the connector registers webhooks against on unit start. |

> **Env-file gotchas.** podman / docker `--env-file` keeps surrounding quotes literally and does not support multi-line values. Always write `GitHub__*` values UNQUOTED, and inline the PEM as one line with `\n` separators. See [Deployment guide § Tier-1 platform credentials](../../docs/guide/deployment.md#tier-1-platform-credentials--github-app-identity-env-only) for the full set of pitfalls.

> **Where do these values come from?** Spring Voyage does not ship a shared App private key — each deployment registers its **own** GitHub App. The fastest path is `spring github-app register`, which drives GitHub's App-from-manifest flow and writes every value above into `deployment/spring.env` for you. The manual github.com flow is also documented end-to-end. See [Register your GitHub App](../../docs/guide/github-app-setup.md) for both paths and the required permission / event matrices.

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
- `GET actions/list-installations` — installations the App can see
  (App-scoped enumeration, used by credential-validation flows). Not
  used by the wizard's repository dropdown — see `list-repositories`.
- `GET actions/list-repositories` — **user-scoped** enumeration: returns
  the intersection of (installations the App is part of) ∩
  (repositories the **signed-in GitHub user** can see) by resolving the
  user's OAuth token through `IGitHubUserAccessTokenProvider` and
  calling `GET /user/installations` + `GET /user/installations/{id}/repositories`.
  When no signed-in user is on the request, returns `401 Unauthorized`
  with a `requires_signin: true` problem extension so the wizard can
  render a "Sign in with GitHub" CTA. The portal sends the OAuth
  session id either via the `oauth_session_id` query parameter or the
  `X-GitHub-OAuth-Session` header. ([#1153](https://github.com/cvoya-com/spring-voyage/issues/1153))
- `GET actions/install-url` — public install URL for the App.
- `GET actions/list-collaborators` — collaborators of the chosen
  repository (used by the Reviewer dropdown).
- `GET config-schema` — JSON Schema describing the per-unit config.
- OAuth authorize / callback / revoke / session endpoints (see
  `Auth/OAuth/`). The callback redirects back to the wizard URL passed
  in the authorize request's `client_state`, embedding the
  `oauth_session_id` and `login` in the URL fragment so they never
  appear in server logs or `Referer` headers.

## User-scoped repository enumeration ([#1153](https://github.com/cvoya-com/spring-voyage/issues/1153))

Pre-#1153 the wizard's repository dropdown enumerated every
installation the App had been granted access to (via
`GET /app/installations`) and intersected it with the App's view of
each installation's repositories. That leaked other users' repos to
any operator who happened to be authenticated against the deployment
— including, in the maintainer's own case, every repo the App had
ever been installed on across every GitHub account.

`GET actions/list-repositories` is now strictly user-scoped:

1. The endpoint resolves the signed-in GitHub user via the injected
   `IGitHubUserAccessTokenProvider`. The OSS implementation
   (`HttpContextGitHubUserAccessTokenProvider`) reads `oauth_session_id`
   from the request, looks the OAuth session up via `IOAuthSessionStore`,
   and resolves the user's access token via `ISecretStore`. Cloud /
   multi-tenant deployments override this provider to resolve the
   token from their own session store.
2. If no user identity is present, the endpoint returns
   `401 Unauthorized` with `requires_signin: true`, an `authorize_path`
   pointing at the OAuth `authorize` endpoint, and `provider: github`
   so the wizard can render a sign-in CTA. The endpoint **never**
   falls back to the App-wide enumeration — that is the bug we fixed.
3. Otherwise the endpoint calls `IGitHubInstallationsClient.ListUserAccessibleInstallationsAsync(userToken)`
   (Octokit `GitHubApps.GetAllInstallationsForCurrentUser`) and, for
   each, `IGitHubInstallationsClient.ListUserAccessibleInstallationRepositoriesAsync(userToken, installationId)`
   (Octokit `GitHubApps.Installation.GetAllRepositoriesForCurrentUser`).
   GitHub itself enforces the intersection of (App is installed) ∩
   (user can see).
4. A per-installation failure does not poison the response — the
   endpoint logs the failure and returns the union of the
   installations that succeeded, so a single bad install does not
   block the operator from picking a different one.
5. An `AuthorizationException` from GitHub (token revoked, expired,
   scope mismatch) is normalised to `401 Unauthorized` +
   `requires_signin: true` so the wizard re-prompts the operator
   instead of leaving them stuck on a 502.
