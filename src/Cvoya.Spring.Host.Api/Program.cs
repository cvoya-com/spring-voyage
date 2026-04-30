// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

using System.Text.Json.Serialization;

using Cvoya.Spring.AgentRuntimes.Claude;
using Cvoya.Spring.AgentRuntimes.Claude.DependencyInjection;
using Cvoya.Spring.AgentRuntimes.Google;
using Cvoya.Spring.AgentRuntimes.Google.DependencyInjection;
using Cvoya.Spring.AgentRuntimes.Ollama;
using Cvoya.Spring.AgentRuntimes.Ollama.DependencyInjection;
using Cvoya.Spring.AgentRuntimes.OpenAI;
using Cvoya.Spring.AgentRuntimes.OpenAI.DependencyInjection;
using Cvoya.Spring.Connector.Arxiv.DependencyInjection;
using Cvoya.Spring.Connector.GitHub;
using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Connector.GitHub.DependencyInjection;
using Cvoya.Spring.Connector.WebSearch.DependencyInjection;
using Cvoya.Spring.Core.CredentialHealth;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.CredentialHealth;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Endpoints;
using Cvoya.Spring.Host.Api.OpenApi;
using Cvoya.Spring.Host.Api.Services;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

// Fail-fast guard: if Build() or RunAsync() throws during host start, log
// the exception and Environment.Exit(1) so the container orchestrator can
// restart the process. Without this, the process can remain alive while
// the host lifetime is broken — podman reports the container as "Up" with
// ExitCode 0, and `unless-stopped` never fires. See #587.
try
{
    var builder = WebApplication.CreateBuilder(args);

    var isLocalDev = args.Contains("--local") ||
        builder.Configuration.GetValue<bool>("LocalDev");

    if (isLocalDev)
    {
        builder.Configuration["LocalDev"] = "true";
    }

    builder.Services
        .AddCvoyaSpringCore()
        .AddCvoyaSpringDapr(builder.Configuration)
        .AddCvoyaSpringAgentRuntimeClaude()
        .AddCvoyaSpringAgentRuntimeGoogle()
        .AddCvoyaSpringAgentRuntimeOllama(builder.Configuration)
        .AddCvoyaSpringAgentRuntimeOpenAI()
        // Phase 2.8 (#682) replaced the legacy Ollama call-site with the new
        // agent-runtime registration above, but one host-side binding still
        // rides on AddCvoyaSpringOllamaLlm: OllamaOptions (consumed by
        // SystemEndpoints for the BaseUrl/timeout knobs operators set via
        // LanguageModel__Ollama__*) and the OllamaConfigurationRequirement
        // startup probe (#616). The new AgentRuntimes:Ollama section +
        // OllamaAgentRuntimeOptions don't feed those legacy seams yet, so
        // keep the legacy registration alongside the runtime one until
        // the API-host code paths are retired — tracked in #728 (follow-up
        // to #711). C1.2b retired /api/v1/ollama/models; the Ollama probe
        // path through SystemEndpoints still consumes OllamaOptions.
        .AddCvoyaSpringOllamaLlm(builder.Configuration)
        .AddCvoyaSpringConnectorGitHub(builder.Configuration)
        .AddCvoyaSpringConnectorArxiv(builder.Configuration)
        .AddCvoyaSpringConnectorWebSearch(builder.Configuration)
        .AddCvoyaSpringApiServices(builder.Configuration);

    // Attach the credential-health watchdog to every plugin-owned named
    // HttpClient that authenticates against a remote service (per
    // CONVENTIONS.md § 16). The wiring lives here rather than inside each
    // plugin's DI extension because the watchdog extension lives in
    // Cvoya.Spring.Dapr (it depends on ICredentialHealthStore + the
    // handler), and plugin projects are constrained by CONVENTIONS.md § 17
    // / AGENTS.md to reference Cvoya.Spring.Core only. The host is the
    // one composition point that knows about every plugin and Dapr, so
    // the fan-out happens here. AddHttpClient(name) is idempotent on the
    // named options entry but accumulates handlers across repeat builders,
    // so re-registering the named client here only attaches the watchdog —
    // it does not reset any configuration the plugin already applied.
    builder.Services.AddHttpClient(ClaudeAgentRuntime.HttpClientName)
        .AddCredentialHealthWatchdog(
            CredentialHealthKind.AgentRuntime,
            subjectId: ClaudeAgentRuntime.RuntimeId,
            secretName: "api-key");
    builder.Services.AddHttpClient(GoogleAgentRuntime.HttpClientName)
        .AddCredentialHealthWatchdog(
            CredentialHealthKind.AgentRuntime,
            subjectId: "google",
            secretName: "api-key");
    builder.Services.AddHttpClient(OpenAiAgentRuntime.HttpClientName)
        .AddCredentialHealthWatchdog(
            CredentialHealthKind.AgentRuntime,
            subjectId: "openai",
            secretName: "api-key");
    // Ollama is typically deployed locally without auth, so the watchdog
    // normally never flips the row. Wiring it anyway covers the reverse-
    // proxy-with-auth deployment shape flagged in ProbeTagsEndpointAsync.
    builder.Services.AddHttpClient(OllamaAgentRuntime.HttpClientName)
        .AddCredentialHealthWatchdog(
            CredentialHealthKind.AgentRuntime,
            subjectId: OllamaAgentRuntime.RuntimeId,
            secretName: "api-key");
    // GitHub: all three named clients (OAuth token exchange, App-auth
    // installation-token mint, Octokit repo-API calls) route through
    // IHttpClientFactory / IHttpMessageHandlerFactory, so the watchdog
    // observes every auth outcome. Per CONVENTIONS.md § 16 the secret-name
    // is the credential key inside the subject — "client-secret" for the
    // OAuth app secret, "private-key" for the App-auth RSA key that signs
    // the JWT (and whose associated installation token Octokit carries).
    builder.Services.AddHttpClient(GitHubOAuthHttpClient.HttpClientName)
        .AddCredentialHealthWatchdog(
            CredentialHealthKind.Connector,
            subjectId: "github",
            secretName: "client-secret");
    builder.Services.AddHttpClient(GitHubAppAuth.HttpClientName)
        .AddCredentialHealthWatchdog(
            CredentialHealthKind.Connector,
            subjectId: "github",
            secretName: "private-key");
    builder.Services.AddHttpClient(GitHubConnector.OctokitHttpClientName)
        .AddCredentialHealthWatchdog(
            CredentialHealthKind.Connector,
            subjectId: "github",
            secretName: "private-key");

    // DataProtection tries to persist/load keys from disk and logs a warning when
    // no stable key directory is configured. During build-time OpenAPI generation
    // (GetDocument.Insider) this is pure noise. Skip registration when running
    // under design-time tooling. See #370.
    if (!BuildEnvironment.IsDesignTimeTooling)
    {
        builder.Services.AddCvoyaSpringDataProtection(builder.Configuration);
    }

    if (isLocalDev)
    {
        builder.Services.AddAuthentication(AuthConstants.LocalDevScheme)
            .AddScheme<AuthenticationSchemeOptions, LocalDevAuthHandler>(AuthConstants.LocalDevScheme, null);
    }
    else
    {
        builder.Services.AddAuthentication(AuthConstants.ApiTokenScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthHandler>(AuthConstants.ApiTokenScheme, null);
    }

    builder.Services.AddAuthorization(options =>
    {
        options.AddUnitPermissionPolicies();
        // Platform-role policies (PlatformOperator / TenantOperator /
        // TenantUser). OSS auth handlers grant all three to every
        // authenticated caller; the cloud overlay scopes per identity via
        // its own IRoleClaimSource. Endpoint-by-endpoint application of
        // these policy names is C1.2b — declared here so the seam is wired
        // before any caller adds .RequireAuthorization(RolePolicies.X).
        options.AddPlatformRolePolicies();
    });
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();

    // Serialize every enum as its string name (case-insensitive on the way in)
    // so clients get "Running" instead of 3 and don't have to reconstruct the
    // numeric ordering. Individual endpoints no longer need to call .ToString().
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.Converters.Add(
            new JsonStringEnumConverter(allowIntegerValues: false));
    });

    builder.Services.AddProblemDetails();
    builder.Services.AddEndpointsApiExplorer();

    // .NET 10's native OpenAPI. The document is emitted as a build artefact
    // via the Microsoft.Extensions.ApiDescription.Server package (configured
    // in the csproj) so the web client's codegen reads from the committed
    // JSON file rather than needing a running server. MapOpenApi still
    // exposes /openapi/v1.json at runtime for introspection.
    builder.Services.AddOpenApi("v1", options =>
    {
        // `decimal` fields round-trip through JSON as plain numbers with our
        // default serialization, but the generator advertises them as
        // `["number", "string"]` to accommodate extreme-precision strings.
        // That poisons every TypeScript consumer (widening the field to
        // `string | number` — see #181). Tighten the contract to `number`
        // only; any client needing the full decimal precision would have
        // to opt in via a custom type.
        options.AddSchemaTransformer((schema, context, _) =>
        {
            var t = context.JsonTypeInfo.Type;
            if (t == typeof(decimal) || t == typeof(decimal?))
            {
                schema.Type = Microsoft.OpenApi.JsonSchemaType.Number;
                schema.Format = "double";
                schema.Pattern = null;
            }
            return Task.CompletedTask;
        });

        // Emit a `servers` entry so Kiota (and other OpenAPI clients) can
        // embed a default base URL rather than forcing every caller to set
        // one on the request adapter. Kiota requires an ABSOLUTE URL to
        // recognise the entry (relative roots like "/" still trigger the
        // "no servers entry" warning). The URL is a development placeholder;
        // every real caller overrides BaseUrl on the request adapter. See
        // #632 for the build-hygiene rationale.
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Servers =
            [
                new Microsoft.OpenApi.OpenApiServer
                {
                    Url = "http://localhost:5000",
                    Description = "Spring Voyage API (development default; override via adapter BaseUrl)",
                },
            ];
            return Task.CompletedTask;
        });

        // Strip the `oneOf:[null, JsonElement]` wrapper the .NET 10 OpenAPI
        // generator emits for nullable JsonElement properties (#1254). The
        // `JsonElement` component schema is `{}` (empty schema, matches
        // anything including null), so the oneOf branches both match a null
        // instance and strict JSON Schema 2020-12 evaluators reject valid
        // wire data. The transformer rewrites the slot to a bare `$ref` so
        // the schema reads as "any JSON value or null" without the
        // ambiguous arithmetic. See JsonElementOneOfNullCleanup for the
        // option-A vs option-B trade-off.
        options.AddDocumentTransformer((document, _, _) =>
        {
            JsonElementOneOfNullCleanup.Apply(document);
            return Task.CompletedTask;
        });
    });

    var app = builder.Build();

    if (app.Environment.IsDevelopment() || isLocalDev)
    {
        app.MapOpenApi();
    }

    app.UseExceptionHandler();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }))
        .WithTags("Health")
        .WithName("Health")
        .ExcludeFromDescription();

    // Auth/token management — TenantUser scope (caller manages their own
    // tokens inside their tenant). C1.2b moved the routes under
    // /api/v1/tenant/auth/.
    app.MapAuthEndpoints();
    // Platform info is deliberately anonymous — the About panel / CLI verb
    // needs to work before a caller has negotiated an auth token. The
    // payload is static version + license metadata; nothing tenant-scoped.
    app.MapPlatformEndpoints();
    // Platform-tenant management surface (#1260 / C1.2d). Self-gates on
    // the PlatformOperator role inside MapPlatformTenantsEndpoints; do
    // NOT add a second .RequireAuthorization() here or the call would
    // re-anchor on the default policy and demote the role gate.
    app.MapPlatformTenantsEndpoints();
    // Tenant-user surface (in-product usage). C1.2b applies the
    // TenantUser role gate via .RequireAuthorization(RolePolicies.TenantUser).
    app.MapAgentEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    app.MapUnitEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    app.MapUnitPolicyEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    app.MapMembershipEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    app.MapPackageEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    app.MapMessageEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    app.MapDirectoryEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    app.MapExpertiseEndpoints();
    app.MapBoundaryEndpoints();
    app.MapOrchestrationEndpoints();
    app.MapUnitExecutionEndpoints();
    app.MapCloneEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    app.MapCloningPolicyEndpoints();
    app.MapCostEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    app.MapTenantCostEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    // Budgets are operator-config — gate on TenantOperator.
    app.MapBudgetEndpoints().RequireAuthorization(RolePolicies.TenantOperator);
    app.MapInitiativeEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    app.MapActivityEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    app.MapThreadEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    app.MapInboxEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    app.MapAnalyticsEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    app.MapDashboardEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    app.MapTenantTreeEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    app.MapMemoriesEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    app.MapSkillsEndpoints().RequireAuthorization(RolePolicies.TenantUser);
    // Connectors: platform-level (PlatformOperator-gated) provision /
    // deprovision. Self-gates internally — do NOT add a second
    // .RequireAuthorization() here or it would demote the role gate.
    app.MapPlatformConnectorEndpoints();
    // Connectors: tenant-level surface uses per-route .RequireAuthorization()
    // gates (mixed read/write surface). The unit-binding pointer routes
    // mounted by MapUnitConnectorPointerEndpoints chain off the units
    // group, which already carries TenantUser.
    app.MapConnectorEndpoints();
    // Agent-runtime install lifecycle — TenantOperator (config / install /
    // uninstall / config update). The full surface lives at
    // /api/v1/tenant/agent-runtimes/installs/. A future PR (follow-up to
    // #1259) introduces a /api/v1/platform/agent-runtimes/ registry view.
    app.MapAgentRuntimeEndpoints().RequireAuthorization(RolePolicies.TenantOperator);
    // Secrets endpoint group covers all three scopes (unit / tenant /
    // platform). The platform-scope routes are gated to PlatformOperator
    // inside the endpoint via ISecretAccessPolicy; here we apply the
    // TenantOperator default — endpoints that write to platform scope
    // also pass through the access-policy gate.
    app.MapSecretEndpoints().RequireAuthorization(RolePolicies.TenantOperator);
    // /api/v1/ollama/models was retired in C1.2b. Callers (CLI / portal)
    // discover Ollama models through the per-runtime install path:
    // GET /api/v1/tenant/agent-runtimes/installs/ollama/models.
    // Provider credential-status probe (#598) feeds the unit-creation
    // wizard's "is this provider configured" banner. Now lives under
    // /api/v1/platform/credentials/{provider}/status — PlatformOperator.
    app.MapSystemEndpoints().RequireAuthorization(RolePolicies.PlatformOperator);
    // #616 startup configuration report. Now under
    // /api/v1/platform/system/configuration. PlatformOperator gated.
    app.MapSystemConfigurationEndpoints().RequireAuthorization(RolePolicies.PlatformOperator);
    // Webhooks (GitHub ingest) authenticate via HMAC, not the API auth
    // pipeline. They sit at /api/v1/webhooks/... outside both scope groups
    // because they're an external ingress, not a user-facing tenant or
    // platform action.
    app.MapWebhookEndpoints();

    await app.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine("FATAL: Host.Api failed to start. Exiting with code 1 so the container orchestrator can restart the process.");
    Console.Error.WriteLine(ex.ToString());
    Environment.Exit(1);
}

/// <summary>
/// Partial class to enable WebApplicationFactory-based integration testing.
/// </summary>
public partial class Program;