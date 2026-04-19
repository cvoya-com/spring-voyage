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

## Reference implementations (PR 1)

Three shipped in PR 1 to cover the spectrum of validation patterns:

1. **`DatabaseConfigurationRequirement`** (`Cvoya.Spring.Dapr`). `IsMandatory=true`. Validates `ConnectionStrings:SpringDb` is set and parseable as a Npgsql connection string. Replaces the hand-rolled throw that used to live inside `AddCvoyaSpringDapr`. Test harnesses that pre-register `DbContextOptions<SpringDbContext>` are detected via the `TestHarnessSignal` marker so they don't need a connection string.
2. **`GitHubAppConfigurationRequirement`** (`Cvoya.Spring.Connector.GitHub`). `IsMandatory=false`. Validates `GITHUB_APP_ID` + `GITHUB_APP_PRIVATE_KEY` + `GITHUB_WEBHOOK_SECRET`. Missing → `Disabled` with a suggestion pointing at `spring github-app register` (issue #631) or manual env vars. Malformed PEM → `Invalid` with a fatal error (carried forward from PR #621's classification). Shares `GitHubAppCredentialsValidator` with the existing `PostConfigure` hook so classification lives in one place. The connector's endpoints consult `GitHubAppConfigurationRequirement.GetCurrentStatus()` instead of the pre-#616 `IGitHubConnectorAvailability` (interface deleted; one seam, not two).
3. **`OllamaConfigurationRequirement`** (`Cvoya.Spring.Dapr`). `IsMandatory` mirrors `LanguageModel:Ollama:RequireHealthyAtStartup`. Probes `GET /api/tags` once at startup; unreachable is `Disabled` in dev (default) and `Invalid` in production (when the flag is on). Replaces the pre-#616 `OllamaHealthCheck` hosted service outright — the framework now owns the probe.

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

## Follow-up: PR 2

The remaining subsystems — Dapr state store, secrets encryption key, dispatcher, container runtime, Rx pipeline, etc. — migrate to the contract in a follow-up PR. The framework shape settles here first so the long tail of subsystems doesn't pile on before the contract is crisp.
