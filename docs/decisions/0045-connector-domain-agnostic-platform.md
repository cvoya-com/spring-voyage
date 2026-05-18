# 0045 — Connectors facilitate flow; they do not replicate upstream subscription configs

- **Status:** Accepted (2026-05-17) — v0.1. Locks the architectural stance that the platform is connector-domain-agnostic. Triggered by the per-repo-webhook-hook cleanup ([#2456](https://github.com/cvoya-com/spring-voyage/issues/2456)) which dropped `GitHubWebhookRegistrar` in favour of GitHub App-level delivery. The principle generalises beyond GitHub: it applies to every future connector (Slack, Linear, …) that has its own subscription / installation model on the upstream side.
- **Date:** 2026-05-17
- **Related:** [#2399](https://github.com/cvoya-com/spring-voyage/issues/2399) (SV-on-SV dogfooding umbrella), [#2456](https://github.com/cvoya-com/spring-voyage/issues/2456) (cleanup that ships this principle in code), [#2424](https://github.com/cvoya-com/spring-voyage/issues/2424) (the three-option design question this ADR settles), [#2425](https://github.com/cvoya-com/spring-voyage/issues/2425) (per-installation routing — folded into #2456), [#2457](https://github.com/cvoya-com/spring-voyage/issues/2457) (v0.2 tenant-network subscriber), [`docs/concepts/connectors.md`](../concepts/connectors.md) (concept doc that references this ADR).
- **Related code:** `src/Cvoya.Spring.Connector.GitHub/Webhooks/GitHubWebhookHandler.cs`, `src/Cvoya.Spring.Connectors.Abstractions/IUnitConnectorBindingLookup.cs`.

## Context

Pre-#2456, the GitHub connector did two unrelated things on a unit's `Starting` → `Running` transition:

1. **Bind the unit to an external system.** Persist the per-unit config (owner / repo / installation id / inbound filters) so inbound deliveries from that system could be addressed to the bound unit.
2. **Configure the upstream system's subscription.** Call `POST /repos/{owner}/{repo}/hooks` against GitHub to create a per-repo webhook whose URL pointed at this deployment, then store the returned `hookId` in a `unit_connector_bindings.metadata` row so `OnUnitStoppingAsync` could call `DELETE /repos/.../hooks/{id}` on teardown.

(2) was a platform-side mirror of GitHub's own configuration surface. The hook lifecycle had to track unit lifecycle, the persisted hook id had to be authenticated against the same installation that created it (fix landed in #2429), and operators had to grant the App `admin:repo_hook` on every repo they wanted SV to listen to.

GitHub Apps already provide the same capability natively: every event in every repo the App is installed on is delivered to the App-wide webhook URL the operator registered when they created the App. The platform-side hook creation duplicated infrastructure GitHub itself maintains; the only thing the platform needs is a way to **route** each inbound delivery to the right unit, which is the binding's job — not the connector's job to install the hook.

The same shape is going to appear in every future connector:

- **Slack:** the operator installs the Slack App on a workspace and invites the bot to channels. The platform doesn't need to call `conversations.invite` on the bot's behalf.
- **Linear:** the operator installs the OAuth integration on a workspace. The platform doesn't need to manage the workspace-scope assignment.
- **GitLab / Bitbucket:** the operator owns project / group selection on the upstream side.

We need a load-bearing principle the platform's connector authors can apply consistently.

## Decision

**Connectors facilitate flow; they do not replicate upstream subscription configs.**

Spring Voyage's connector contract is scoped to four concerns:

1. **Bind a unit to an external entity.** Persist a per-unit typed config that names what the unit is connected to (owner / repo for GitHub, workspace / channel for Slack, …).
2. **Receive events from the external system.** Listen on whatever ingress the upstream system delivers to — App-level webhook URL, OAuth-authorised push channel, polling loop.
3. **Route each received event to the bound unit.** Match the event's payload coordinates against per-unit bindings; deliver the translated message to the matched unit; **drop unbound deliveries silently**.
4. **Provide outbound credentials and ergonomic helpers to agent containers** via `IConnectorRuntimeContextContributor` and `IConnectorPromptContextContributor` (ADR-0029-aligned; ADR-0027 conformance).

What connectors **do not** do:

- **Manage the upstream system's subscription model.** Creating GitHub repo hooks, joining Slack channels, attaching Linear webhooks, polling-rate negotiation with the upstream provider — all of these are operator-owned on the upstream's own surface. The platform does not replicate, reconcile, or persist this configuration.
- **Persist hook / subscription ids that mirror upstream state.** If the upstream system already knows what it's delivering and where, there is no platform side of the audit trail to maintain. State that exists solely as a mirror of upstream config gets deleted.

The operator owns upstream scope through the upstream's own surface (which repos the GitHub App is installed on, which channels the Slack App is invited to). The platform receives the events the upstream chooses to deliver, routes the ones it has bindings for, and drops the ones it doesn't.

## Consequences

### What this buys

- **One fewer layer of state to keep in sync.** Pre-#2456, an out-of-band edit to a GitHub App's installation scope could leave the platform's persisted `hookId` referring to a hook that was already deleted by some other admin path. With this principle, that drift cannot exist — there is no platform-side hook id.
- **Generalises to every connector.** A new connector author doesn't relitigate "should I create a webhook for them?" — the answer is no, and the abstractions reflect that.
- **Smaller blast radius on credential rotation.** A leaked App-installation token cannot delete arbitrary hooks that the platform created — because the platform never created any.
- **Smaller per-unit lifecycle surface.** `OnUnitStartingAsync` / `OnUnitStoppingAsync` become no-ops for connectors that follow this principle (which is most of them); the platform-side connector type stays small.
- **No "missing scope" mode.** The connector cannot fail at `Starting` because the App's permissions don't include `admin:repo_hook` — that scope is no longer required for the connector to function.

### What this costs

- **Wasted work on unbound events.** App-level delivery means the platform receives every event for every repo the App is installed on. Repos no unit is bound to generate webhook traffic the platform has to validate, parse, and silently drop. For typical OSS deployments this is negligible; for high-volume orgs it is a measurable cost.
  - **Mitigation:** operators control upstream scope. Installing the App on only the repos that have units bound to them reduces the wasted-work surface to zero.
  - **Diagnostic surface:** unbound drops are logged at `Information` with the `(installation_id, owner, repo)` triple so operators can correlate noise.
  - **Future:** a v0.2 tenant-network subscriber (#2457) can re-introduce per-repo precision when the cost of App-level delivery becomes load-bearing for a given deployment. That work is **opt-in**, **per-tenant**, and does not move the platform's connector contract back to where it was.
- **No platform-side audit trail of upstream subscription state.** When an admin deletes the App from a repo on github.com, the platform learns by no longer receiving events. There is no "the App was uninstalled at HH:MM" row to query.
  - **Mitigation:** the GitHub App's audit log on github.com is the system of record. Operators wanting that signal in SV can ingest `installation` / `installation_repositories` lifecycle events (the connector already translates them).

### Inverse principle: what you DO build

The line is **flow vs configuration**:

| You build the platform-side surface for…                           | You don't build the platform-side surface for…                  |
|--------------------------------------------------------------------|------------------------------------------------------------------|
| Receiving an inbound event                                          | Telling the upstream which events to send                       |
| Routing the event to the bound unit                                 | Persisting the upstream's subscription id                       |
| Translating the payload into a platform `Message`                   | Mirroring the upstream's permission model                       |
| Validating signatures / OAuth tokens that authenticate the event    | Granting the upstream access scopes on a per-unit basis         |
| Providing outbound credentials to the agent container               | Replicating the upstream's installation / workspace assignment  |

If you find yourself writing platform code that calls the upstream's "configure subscription" API — `POST /repos/{owner}/{repo}/hooks`, `conversations.invite`, `webhooks.create` — that work belongs on the upstream's own surface, in the operator's hands, not in the platform's connector type.

## Alternatives considered

- **Keep the per-repo registrar (status quo pre-#2456).** Operator wouldn't need to install the GitHub App on every repo they wanted SV to listen to — the connector would call `POST /repos/.../hooks` for them at bind time. Rejected because it duplicates upstream config the operator already owns and forces every connector author to design their own version of this layer.
- **Keep the registrar opt-in via a flag.** Lets operators who want per-repo precision keep the existing path. Rejected — it bifurcates the contract and forces the platform to maintain both paths forever (v0.1 has no released deployments to back-compat against).
- **Move the per-repo subscriber to a tenant-network worker (#2457).** Same per-repo precision, but the subscription happens inside the tenant's network from a worker co-hosted with the agent runtime. This is genuinely v0.2-shaped because the tenant-network worker doesn't exist yet, and it does NOT relitigate this ADR — the platform's *contract* still says "connectors don't replicate upstream subscription configs"; the tenant-network worker is a *tenant-scoped helper* on the operator's side of that contract.
