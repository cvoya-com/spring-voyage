# Startup Configuration Validation

> **[Architecture Index](README.md)** | Related: [Security](security.md), [Deployment](deployment.md), [CLI & Web](cli-and-web.md)

---

## Problem

Spring Voyage reads ~25 tier-1 config values — platform identity, infrastructure bindings, service wiring — across Database / Dapr / GitHub / AI / Ollama / Secrets / Dispatcher / ContainerRuntime / WorkflowOrchestration subsystems. Historically each subsystem validated (or failed to validate) its own config on its own schedule: fail-fast at startup for one, fail-at-first-use for the next, silent default with warning for a third. An operator hitting an unconfigured feature got a 502, a clean 404, or a log-only warning depending on which subsystem they touched. Operators also had no place to look post-deploy to answer "is the platform deployed correctly?".

Issue [#616](https://github.com/cvoya-com/spring-voyage/issues/616) generalises the GitHub-specific availability seam ([PR #621's `IGitHubConnectorAvailability`](https://github.com/cvoya-com/spring-voyage/pull/621)) into a cross-subsystem framework and surfaces the result to operators over HTTP, CLI, and the portal.

## Contract

All types live under `Cvoya.Spring.Core.Configuration`:

```csharp
public interface IConfigurationRequirement
{
    string RequirementId { get; }          // "github-app-credentials"
    string DisplayName { get; }            // "GitHub App credentials"
    string SubsystemName { get; }          // "GitHub Connector"
    bool   IsMandatory { get; }            // host won't start without this if true
    IReadOnlyList<string> EnvironmentVariableNames { get; }
    string? ConfigurationSectionPath { get; }
    string  Description { get; }
    Uri?    DocumentationUrl { get; }
    Task<ConfigurationRequirementStatus> ValidateAsync(CancellationToken ct);
}

public enum ConfigurationStatus { Met, Disabled, Invalid }
public enum SeverityLevel      { Information, Warning, Error }
```

`ConfigurationRequirementStatus` carries the outcome: `Status`, `Severity`, `Reason`, `Suggestion`, and an optional `FatalError` (thrown by the validator when the requirement is mandatory and the status is `Invalid`).

The **`IStartupConfigurationValidator`** seam exposes the cached `ConfigurationReport` to consumers (HTTP endpoint, CLI, portal page). The default implementation is `Cvoya.Spring.Dapr.Configuration.StartupConfigurationValidator` — an `IHostedService` that runs during `StartAsync`, enumerates every registered `IConfigurationRequirement`, and caches the aggregated report.

## Validation policy

The validator computes a per-subsystem and overall `ConfigurationReportStatus`:

| Inputs                                                         | Report status |
|----------------------------------------------------------------|---------------|
| Every requirement `Met` with `Information`                     | `Healthy`     |
| At least one `Disabled` or a `Met + Warning`                   | `Degraded`    |
| At least one `Invalid` (mandatory aborts before this is read)  | `Failed`      |

Fail-fast rules:

- `IsMandatory=true` + `Status=Invalid` → the validator's `StartAsync` throws the requirement's `FatalError` (or a synthesised `InvalidOperationException` if none was supplied). Multiple fatal failures are aggregated into one `AggregateException` so operators see every problem in a single boot attempt.
- `IsMandatory=false` + `Status=Invalid` → report flags the subsystem as broken, host keeps booting; dependent features register themselves as disabled.
- `IsMandatory=false` + `Status=Disabled` → report flags the feature as off, host keeps booting.
- `Status=Met + Severity=Warning` → the "met but degraded" case (e.g. ephemeral dev AES key, default Ollama endpoint). Host keeps booting; report surfaces the caveat.

The validator runs **once** at host startup and caches the report for the lifetime of the host. There is no revalidation endpoint in PR 1 — tier-1 values are immutable after boot. A `POST /system/configuration/revalidate` can be added as a follow-up if an operator-initiated refresh becomes valuable.

## Registration

Each subsystem registers its requirements from inside its own `AddCvoyaSpring*` extension method:

```csharp
services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IConfigurationRequirement, MySubsystemConfigurationRequirement>());
```

`AddCvoyaSpringDapr` registers the validator itself (via `AddCvoyaSpringConfigurationValidator`) before any requirement, so the `IHostedService` slot belongs to the validator and every subsystem's `AddAll*` pulls the same instance via DI.

## Reference implementations

The framework ships with seven reference implementations covering the spectrum
of validation patterns. Three landed with the framework in PR 1 (#616 / #638);
four joined in PR 2 (#639):

1. **`DatabaseConfigurationRequirement`** (`Cvoya.Spring.Dapr`). `IsMandatory=true`. Validates `ConnectionStrings:SpringDb` is set and parseable as a Npgsql connection string. Replaces the hand-rolled throw that used to live inside `AddCvoyaSpringDapr`. Test harnesses that pre-register `DbContextOptions<SpringDbContext>` are detected via the `TestHarnessSignal` marker so they don't need a connection string.
2. **`GitHubAppConfigurationRequirement`** (`Cvoya.Spring.Connector.GitHub`). `IsMandatory=false`. Validates `GitHub__AppId` + `GitHub__PrivateKeyPem` + `GitHub__WebhookSecret` (the .NET `Section__Key` env var convention — `GITHUB_APP_*` short forms are NOT consumed). Missing → `Disabled` with a suggestion pointing at `spring github-app register` (issue #631) or manual env vars. Malformed PEM → `Invalid` with a fatal error (carried forward from PR #621's classification). The validator decodes literal `\n` escapes in the PEM and strips a single layer of surrounding quotes (#1186) so podman / docker `--env-file` users — which keeps quotes literally and forbids multi-line values — can use the same encoding Firebase / GCP service-account keys use. Shares `GitHubAppCredentialsValidator` with the existing `PostConfigure` hook so classification lives in one place. The connector's endpoints consult `GitHubAppConfigurationRequirement.GetCurrentStatus()` instead of the pre-#616 `IGitHubConnectorAvailability` (interface deleted; one seam, not two).
3. **`OllamaConfigurationRequirement`** (`Cvoya.Spring.Dapr`). `IsMandatory` mirrors `LanguageModel:Ollama:RequireHealthyAtStartup`. Probes `GET /api/tags` once at startup; unreachable is `Disabled` in dev (default) and `Invalid` in production (when the flag is on). Replaces the pre-#616 `OllamaHealthCheck` hosted service outright — the framework now owns the probe.
4. **`DaprStateStoreConfigurationRequirement`** (`Cvoya.Spring.Dapr`, PR 2). `IsMandatory=true`. Validates `DaprStateStore:StoreName` is a non-empty component name. The OSS default is `"statestore"` so most deployments stay on the happy path; a blank value now fails at boot rather than at first Dapr call.
5. **`SecretsConfigurationRequirement`** (`Cvoya.Spring.Dapr`, PR 2). `IsMandatory=true`. Lifts the `SecretsEncryptor` key-source classification (`SPRING_SECRETS_AES_KEY` → `Secrets:AesKeyFile` → ephemeral) into a declarative requirement. Ephemeral dev key (`Secrets:AllowEphemeralDevKey=true`) is the canonical `Met + Warning` case — the host boots, secrets work, but operators are reminded that restarts render existing envelopes unreadable. Weak-key / malformed / missing-file classifications surface as `Invalid` with a fatal error. Classification lives in `SecretsKeyClassifier`, shared with the encryptor's constructor self-check.
6. **`DispatcherConfigurationRequirement`** (`Cvoya.Spring.Dapr`, PR 2). `IsMandatory=false`. Validates `Dispatcher:BaseUrl` is a well-shaped absolute HTTP(S) URI and warns when `Dispatcher:BearerToken` is empty. Missing `BaseUrl` → `Disabled` (dispatcher-dependent features declare themselves unavailable but the host still boots). Malformed URL → `Invalid`. Replaces the fail-at-first-use throw inside `DispatcherClientContainerRuntime.CreateClient`.
7. **`ContainerRuntimeConfigurationRequirement`** (`Cvoya.Spring.Dapr`, PR 2). `IsMandatory=false`. Validates `ContainerRuntime:RuntimeType` against the supported set (`podman`, `docker`). Invalid values report `Invalid` without aborting boot so non-dispatcher hosts (Worker, API) keep running.

## Surfaces

- **HTTP:** `GET /api/v1/system/configuration` returns the cached `ConfigurationReport` as JSON. Anonymous in the OSS build; the private cloud host can layer `RequireAuthorization()` on top via middleware.
- **CLI:** `spring system configuration` prints the report as a table; `--json` prints raw JSON; `spring system configuration <subsystem>` drills into one section.
- **Portal:** `/system/configuration` page lists each subsystem as a collapsible card with a status badge (Healthy / Degraded / Failed), expandable requirement rows carrying name + status + severity + reason + suggestion + docs link.

## Extending the framework

Adding a requirement for a new subsystem:

1. Implement `IConfigurationRequirement` — usually a small `public sealed class` in the subsystem's project. Return `Met`, `MetWithWarning`, `Disabled`, or `Invalid` helpers from `ConfigurationRequirementStatus`.
2. Register it inside the subsystem's `AddCvoyaSpring*` extension:
   ```csharp
   services.TryAddSingleton<MySubsystemConfigurationRequirement>();
   services.TryAddEnumerable(
       ServiceDescriptor.Singleton<IConfigurationRequirement, MySubsystemConfigurationRequirement>(
           sp => sp.GetRequiredService<MySubsystemConfigurationRequirement>()));
   ```
3. If the subsystem exposes its own endpoints that need to short-circuit on "disabled", resolve the concrete requirement from DI and call its status helper rather than re-querying the validator's report.

## Out of scope

- **Tier-2 tenant-default credentials** (LLM API keys). Those live behind `ILlmCredentialResolver` (PR #619) and the tenant-defaults panel. Mixing them into the tier-1 framework dilutes both — `/system/configuration` is "is the platform deployed correctly?", not "is this tenant's workload configured?".
- **Dapr sidecar availability.** Not config — orchestration health. Kubernetes readiness probes and docker-compose `depends_on` handle "is the sidecar up"; a config requirement for Dapr would always report Met because the platform already aborts at startup if it can't reach the sidecar.
- **Tenant-aware validation.** The OSS framework is single-tenant. The private cloud host can substitute a tenant-scoped `IStartupConfigurationValidator` (or wrap the endpoint with tenant-filtering middleware) by pre-registering before `AddCvoyaSpringDapr`.
- **Revalidation endpoint.** Out of scope for PR 1; file a follow-up if operator-initiated refresh is useful.

## Subsystems intentionally not migrated

A handful of Spring Voyage options classes were evaluated during #639 and left outside the framework because the cost/benefit didn't land:

- **Rx activity pipeline** (`StreamEventPublisherOptions`). The only knob is `PubSubName`, which defaults to `"pubsub"` and is resolved via Dapr pub/sub — Dapr surfaces the component-not-found error on first publish and a host-level validator would only duplicate that signal without adding value.
- **AI provider (`AiProviderOptions.ApiKey`, base URL)**. Tenant-default LLM credentials are tier-2 concerns (`ILlmCredentialResolver`, #619) — surfacing them through the tier-1 framework would dilute the "is the platform deployed correctly?" question the framework is designed to answer.
- **Workflow orchestration (`WorkflowOrchestrationOptions.ContainerImage`, timeouts)**. These are per-unit manifest values, not host-wide deployment config; they surface through the orchestration-strategy resolver when the relevant unit is run.
