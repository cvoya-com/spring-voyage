// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api;

using System.Text.Json.Serialization;

using Cvoya.Spring.AgentRuntimes.DependencyInjection;
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
using Cvoya.Spring.Host.Api.Endpoints.Otlp;
using Cvoya.Spring.Host.Api.OpenApi;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.ModelProviders.DependencyInjection;
using Cvoya.Spring.RuntimeCatalog.DependencyInjection;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

public partial class Program
{
    public static async Task Main(string[] args)
    {
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
                // ADR-0038: the catalogue must register BEFORE AddCvoyaSpringDapr
                // because downstream services (the install seed provider, the
                // dispatcher's launcher resolution) consult IRuntimeCatalog at
                // construction. The latter TryAdds an empty fallback so the real
                // catalogue must be in DI first.
                .AddCvoyaSpringRuntimeCatalog()
                .AddCvoyaSpringDapr(builder.Configuration)
                // ADR-0038 (#1761) collapsed the four per-provider AgentRuntime
                // projects into runtime-catalog.yaml + Cvoya.Spring.ModelProviders
                // adapters + Cvoya.Spring.AgentRuntimes launcher consolidation.
                .AddCvoyaSpringModelProviders()
                .AddCvoyaSpringAgentRuntimes()
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
            // ADR-0038 (#1761): per-runtime credential-health watchdog wiring
            // moves to a per-provider model-provider-adapter wiring in Chunk 2 of
            // PR-1a, when the credential resolver re-keys from (tenant, runtime)
            // to (tenant, provider, authMethod). The wiring is gone in Chunk 1
            // because the per-runtime HttpClient names came from the deleted
            // ClaudeAgentRuntime/GoogleAgentRuntime/OpenAiAgentRuntime/OllamaAgentRuntime
            // classes.
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
                    .AddScheme<AuthenticationSchemeOptions, LocalDevAuthHandler>(AuthConstants.LocalDevScheme, null)
                    // OTLP ingest endpoints (#2492) validate per-invocation
                    // callback JWTs the launcher injects into the runtime
                    // container. Registered as a sibling scheme so the auth
                    // pipeline tries the JWT handler when the request hits
                    // /otlp/v1/* (the endpoint explicitly requires this scheme).
                    .AddScheme<AuthenticationSchemeOptions, OtlpCallbackAuthHandler>(AuthConstants.OtlpCallbackScheme, null);
            }
            else
            {
                builder.Services.AddAuthentication(AuthConstants.ApiTokenScheme)
                    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthHandler>(AuthConstants.ApiTokenScheme, null)
                    // OTLP ingest endpoints (#2492) — see the local-dev branch above.
                    .AddScheme<AuthenticationSchemeOptions, OtlpCallbackAuthHandler>(AuthConstants.OtlpCallbackScheme, null);
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
            //
            // Per #1629 every public Guid field rides the wire in the canonical
            // 32-char lowercase no-dash hex form (matches Address.Path /
            // GuidFormatter). The JsonConverter is registered here so every DTO that
            // ships a `Guid` property — including the ones that flipped from
            // `string` in PR5 — emits the same shape Address.ToString() emits.
            // Parse stays lenient (accepts both dashed and no-dash); emit is strict.
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.Converters.Add(
                    new JsonStringEnumConverter(allowIntegerValues: false));
                options.SerializerOptions.Converters.Add(
                    new Cvoya.Spring.Host.Api.Serialization.NoDashGuidJsonConverter());
                options.SerializerOptions.Converters.Add(
                    new Cvoya.Spring.Host.Api.Serialization.NullableNoDashGuidJsonConverter());
            });

            builder.Services.AddProblemDetails(options =>
            {
                // #2250: surface the structured detail when Address.For rejects
                // a non-Guid path parameter. The framework default leaves
                // ProblemDetails.Detail empty for synthesised 4xx responses,
                // which renders a useless "Bad Request" in the portal. The
                // exception message is safe to echo — it only references the
                // path segment the caller already provided.
                options.CustomizeProblemDetails = ctx =>
                {
                    var ex = ctx.HttpContext.Features
                        .Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
                    if (ex is Cvoya.Spring.Core.Messaging.InvalidAddressIdException invalidId)
                    {
                        ctx.ProblemDetails.Detail = invalidId.Message;
                        ctx.ProblemDetails.Extensions["scheme"] = invalidId.Scheme;
                        ctx.ProblemDetails.Extensions["idString"] = invalidId.IdString;
                    }
                };
            });
            builder.Services.AddEndpointsApiExplorer();

            // .NET 10's native OpenAPI. The document is emitted as a build artefact
            // via the Microsoft.Extensions.ApiDescription.Server package (configured
            // in the csproj) so the web client's codegen reads from the committed
            // JSON file rather than needing a running server. MapOpenApi still
            // exposes /openapi/v1.json at runtime for introspection.
            builder.Services.AddOpenApi("v1", options =>
            {
                // Numeric primitives round-trip through JSON as plain numbers with
                // our default serialization, but .NET 10's OpenAPI generator
                // advertises them as `["<numeric>", "string"]` unions to reflect
                // STJ's "may read from string" tolerance. That poisons every
                // TypeScript consumer (widening fields to `string | number` — see
                // #181) and breaks Kiota: it can't reconcile a union with
                // `format: int32`/`int64` and silently falls back to `string`.
                // Tighten the contract to the numeric type only; clients needing
                // string-encoded numbers would have to opt in via a custom type.
                // Nullable variants remain nullable so the wire shape and C# model
                // agree (closing #1367 for decimal; same logic for the rest).
                options.AddSchemaTransformer((schema, context, _) =>
                {
                    var t = context.JsonTypeInfo.Type;
                    if (t == typeof(decimal))
                    {
                        schema.Type = Microsoft.OpenApi.JsonSchemaType.Number;
                        schema.Format = "double";
                        schema.Pattern = null;
                    }
                    else if (t == typeof(decimal?))
                    {
                        schema.Type = Microsoft.OpenApi.JsonSchemaType.Number | Microsoft.OpenApi.JsonSchemaType.Null;
                        schema.Format = "double";
                        schema.Pattern = null;
                    }
                    else if (t == typeof(double) || t == typeof(float))
                    {
                        schema.Type = Microsoft.OpenApi.JsonSchemaType.Number;
                        schema.Pattern = null;
                    }
                    else if (t == typeof(double?) || t == typeof(float?))
                    {
                        schema.Type = Microsoft.OpenApi.JsonSchemaType.Number | Microsoft.OpenApi.JsonSchemaType.Null;
                        schema.Pattern = null;
                    }
                    else if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
                          || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte))
                    {
                        schema.Type = Microsoft.OpenApi.JsonSchemaType.Integer;
                        schema.Pattern = null;
                    }
                    else if (t == typeof(int?) || t == typeof(long?) || t == typeof(short?) || t == typeof(byte?)
                          || t == typeof(uint?) || t == typeof(ulong?) || t == typeof(ushort?) || t == typeof(sbyte?))
                    {
                        schema.Type = Microsoft.OpenApi.JsonSchemaType.Integer | Microsoft.OpenApi.JsonSchemaType.Null;
                        schema.Pattern = null;
                    }
                    // #1629 PR5: every public Guid field rides the wire in the
                    // canonical 32-character no-dash hex form (see
                    // NoDashGuidJsonConverter and Cvoya.Spring.Core.Identifiers.GuidFormatter).
                    // Microsoft.AspNetCore.OpenApi advertises Guid-typed fields as
                    // { "type": "string", "format": "uuid" } by default, which is
                    // the right marker for "stable identifier" — Kiota and
                    // openapi-typescript both treat the format as opaque-string,
                    // so the deviation from strict v4-uuid-with-dashes has no
                    // generated-client cost. The custom converter handles the
                    // wire-shape difference at runtime.
                    else if (t == typeof(Guid))
                    {
                        schema.Type = Microsoft.OpenApi.JsonSchemaType.String;
                        schema.Format = "uuid";
                        schema.Pattern = null;
                    }
                    else if (t == typeof(Guid?))
                    {
                        schema.Type = Microsoft.OpenApi.JsonSchemaType.String | Microsoft.OpenApi.JsonSchemaType.Null;
                        schema.Format = "uuid";
                        schema.Pattern = null;
                    }
                    // Microsoft.AspNetCore.OpenApi 10.0 emits `{ "type": "array" }`
                    // (no `items`) for IReadOnlyList<Guid> / IList<Guid> / Guid[],
                    // which Kiota cannot translate to a typed C# list — it falls
                    // back to `UntypedNode`. Plug the items slot for any
                    // IEnumerable<Guid> so the generated client surfaces a
                    // strongly-typed `List<Guid>`. Preserves the schema's existing
                    // nullability bit (so an `IReadOnlyList<Guid>?` property keeps
                    // `["null", "array"]` while gaining proper items).
                    else if (IsGuidEnumerable(t))
                    {
                        // Keep any pre-set Null bit; ensure Array bit is set.
                        schema.Type = Microsoft.OpenApi.JsonSchemaType.Array
                            | (schema.Type & Microsoft.OpenApi.JsonSchemaType.Null);
                        schema.Items = new Microsoft.OpenApi.OpenApiSchema
                        {
                            Type = Microsoft.OpenApi.JsonSchemaType.String,
                            Format = "uuid",
                        };
                    }
                    return Task.CompletedTask;

                    static bool IsGuidEnumerable(Type type)
                    {
                        if (type == typeof(Guid[]))
                        {
                            return true;
                        }

                        if (!type.IsGenericType)
                        {
                            return false;
                        }

                        var args = type.GetGenericArguments();
                        if (args.Length != 1 || args[0] != typeof(Guid))
                        {
                            return false;
                        }

                        return typeof(System.Collections.Generic.IEnumerable<Guid>).IsAssignableFrom(type);
                    }
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

                // Rewrite the `oneOf: [{ "type": "null" }, { "$ref": ... }]` shape on
                // the unit-policy slots so Kiota's CSharp generator produces plain
                // nullable references rather than `IComposedTypeWrapper` wrappers
                // whose `CreateFromDiscriminatorValue` reads an empty-string
                // discriminator and never populates the inner sub-record (#999 root
                // cause; original symptom prompted the bypass at ApiClient.cs).
                //
                // This transform is intentionally surgical — it only rewrites the
                // eight property slots actually consumed by the unit-policy CLI
                // surface. Other `oneOf [null, $ref]` patterns (e.g. on the
                // TypeScript-facing connector / agent / boundary surfaces) stay
                // untouched so we do not destabilise downstream clients that do
                // not exhibit the Kiota CSharp regression.
                options.AddDocumentTransformer((document, _, _) =>
                {
                    if (document.Components?.Schemas is not { } schemas)
                    {
                        return Task.CompletedTask;
                    }

                    static Microsoft.OpenApi.OpenApiSchemaReference? RewriteNullableRef(
                        Microsoft.OpenApi.IOpenApiSchema? slot,
                        Microsoft.OpenApi.OpenApiDocument doc)
                    {
                        if (slot is not Microsoft.OpenApi.OpenApiSchema concrete) return null;
                        if (concrete.OneOf is not { Count: 2 } oneOf) return null;

                        Microsoft.OpenApi.OpenApiSchemaReference? refBranch = null;
                        var hasNullBranch = false;
                        foreach (var branch in oneOf)
                        {
                            if (branch is Microsoft.OpenApi.OpenApiSchema c
                                && c.Type == Microsoft.OpenApi.JsonSchemaType.Null)
                            {
                                hasNullBranch = true;
                            }
                            else if (branch is Microsoft.OpenApi.OpenApiSchemaReference r)
                            {
                                refBranch = r;
                            }
                        }

                        if (!hasNullBranch || refBranch is null) return null;
                        // Preserve the original target id; rebind to the document so
                        // the reference resolves cleanly in the rewritten document.
                        return new Microsoft.OpenApi.OpenApiSchemaReference(
                            refBranch.Reference?.Id ?? string.Empty,
                            doc);
                    }

                    static void Patch(
                        Microsoft.OpenApi.OpenApiDocument doc,
                        System.Collections.Generic.IDictionary<string, Microsoft.OpenApi.IOpenApiSchema> schemaMap,
                        string schemaName,
                        System.Collections.Generic.IReadOnlyList<string> propertyNames)
                    {
                        if (!schemaMap.TryGetValue(schemaName, out var rawSchema)) return;
                        if (rawSchema is not Microsoft.OpenApi.OpenApiSchema concrete) return;
                        if (concrete.Properties is not { } properties) return;

                        foreach (var propertyName in propertyNames)
                        {
                            if (!properties.TryGetValue(propertyName, out var slot)) continue;
                            var rewritten = RewriteNullableRef(slot, doc);
                            if (rewritten is not null)
                            {
                                properties[propertyName] = rewritten;
                            }
                        }
                    }

                    Patch(
                        document,
                        schemas,
                        "UnitPolicyResponse",
                        new[] { "skill", "model", "cost", "executionMode", "initiative" });
                    Patch(
                        document,
                        schemas,
                        "ExecutionModePolicy",
                        new[] { "forced" });
                    Patch(
                        document,
                        schemas,
                        "InitiativePolicy",
                        new[] { "tier1", "tier2" });

                    return Task.CompletedTask;
                });
            });

            var app = builder.Build();

            if (app.Environment.IsDevelopment() || isLocalDev)
            {
                app.MapOpenApi();
            }

            // BadHttpRequestException carries a StatusCode (400 for malformed
            // request bodies — e.g. a JsonException raised from a custom
            // JsonConverter such as NoDashGuidJsonConverter — rejected by
            // RequestDelegateFactory.TryReadBodyAsync). The default
            // ExceptionHandlerMiddleware ignores that property and emits 500;
            // the StatusCodeSelector below honours it so deserialization
            // failures surface as a clean 400. InvalidAddressIdException
            // (raised by Address.For when a path segment isn't Guid-shaped)
            // gets the same 400 treatment — see #2250. Other exception types
            // fall through to the framework default (500). See #1644.
            app.UseExceptionHandler(new ExceptionHandlerOptions
            {
                StatusCodeSelector = ex => ex switch
                {
                    BadHttpRequestException badRequest => badRequest.StatusCode,
                    Cvoya.Spring.Core.Messaging.InvalidAddressIdException => StatusCodes.Status400BadRequest,
                    _ => StatusCodes.Status500InternalServerError,
                },
            });
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapGet("/health", () => Results.Ok(new { Status = "Healthy" }))
                .WithTags("Health")
                .WithName("Health")
                .ExcludeFromDescription();

            // Auth/token management — TenantUser scope (caller manages their own
            // tokens inside their tenant). C1.2b moved the routes under
            // /api/v1/tenant/auth/. C1.2 audit: role gate is self-applied inside
            // MapAuthEndpoints via .RequireAuthorization(RolePolicies.TenantUser)
            // on the group — do NOT add a second call here.
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
            // #2409: team-role membership routes (ADR-0044 § 3). The handlers
            // self-gate Owner / Viewer via UnitPermissionCheck, so only the
            // TenantUser authentication gate is applied here. Mounted as a
            // sibling group so the {id}/members/humans sub-route can carry
            // its own OpenAPI metadata.
            app.MapUnitTeamMembershipEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            // ADR-0046 §7 / #2266 / #2267: the per-Human read-side envelope.
            // Lives at /api/v1/tenant/humans/{id} and stays there per
            // ADR-0047 §14 — it remains a unit-membership concern owned
            // by ADR-0046.
            app.MapHumanEnvelopeEndpoints().RequireAuthorization(RolePolicies.TenantUser);

            // ADR-0047 §2 / §14: connector-identity routes relocate onto the
            // TenantUser principal under /api/v1/tenant/users/{id}/identities.
            // The TenantUser envelope (GET / PATCH /api/v1/tenant/users/{id})
            // lives on the same endpoint group.
            app.MapTenantUserIdentityEndpoints().RequireAuthorization(RolePolicies.TenantUser);

            // ADR-0047 §14: 410 Gone stub on the prior
            // /api/v1/tenant/humans/{id}/identities routes with a structured
            // migration hint pointing at the new path. Anonymous (no auth
            // requirement) so a calling CLI that's behind on auth still
            // surfaces the migration hint instead of a 401.
            app.MapRetiredHumanIdentityEndpoints();
            app.MapUnitPolicyEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            app.MapMembershipEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            app.MapPackageEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            app.MapPackageExportEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            app.MapPackageInstallEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            app.MapMessageEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            app.MapDirectoryEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            app.MapExpertiseEndpoints();
            app.MapBoundaryEndpoints();
            app.MapLegacyOrchestrationEndpoints();
            // ADR-0048 / ADR-0049 / #2586: the agent-facing messaging
            // callback surface (sv.messaging.send / sv.messaging.broadcast +
            // the MCP streamable-HTTP handler). Relocated from the dispatcher
            // — which has no Dapr sidecar and cannot invoke actors — onto this
            // Dapr-connected host. The endpoints own their auth via CallbackTokenValidator
            // (the per-invocation callback JWT the launcher injects), so no
            // .RequireAuthorization() chain — the API-token / OTLP schemes
            // do not apply here.
            app.MapOrchestrationCallbackEndpoints();
            app.MapUnitExecutionEndpoints();
            app.MapCloneEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            app.MapCloningPolicyEndpoints();
            app.MapCostEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            app.MapTenantCostEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            // Budgets are operator-config — each route group inside
            // MapBudgetEndpoints self-gates on TenantOperator (agent +
            // tenant + unit). Do NOT chain a second .RequireAuthorization
            // here; the previous wiring only bound the role gate to the
            // returned agent group, leaving the tenant- and unit-scope
            // groups unauthenticated (#2288).
            app.MapBudgetEndpoints();
            app.MapInitiativeEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            app.MapActivityEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            // Activity-capture tenant settings (#2492). TenantOperator gate
            // — capture level and retention horizon are operator-controlled,
            // not per-user.
            app.MapTenantActivitySettingsEndpoints().RequireAuthorization(RolePolicies.TenantOperator);
            app.MapThreadEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            app.MapInboxEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            app.MapAnalyticsEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            app.MapDashboardEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            app.MapTenantTreeEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            app.MapMemoriesEndpoints().RequireAuthorization(RolePolicies.TenantUser);
            // #2160: operational-issues read surface (Overview + CLI consumer).
            app.MapIssuesEndpoints().RequireAuthorization(RolePolicies.TenantUser);
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
            // Model-provider install lifecycle — TenantOperator (config /
            // install / uninstall / config update). The full surface lives at
            // /api/v1/tenant/model-providers/installs/ per ADR-0038.
            app.MapModelProviderEndpoints().RequireAuthorization(RolePolicies.TenantOperator);
            // Secrets endpoint group covers all three scopes (unit / tenant /
            // platform). Role gates are applied per-group inside SecretEndpoints:
            // unit/tenant groups → TenantOperator; platform group → PlatformOperator.
            // A second ISecretAccessPolicy gate enforces scope-shaped checks on top.
            // Do NOT chain a second .RequireAuthorization() here — the groups
            // self-gate internally and the returned unit group already carries
            // TenantOperator; a second call would override it with the default
            // policy rather than elevating it.
            app.MapSecretEndpoints();
            // /api/v1/ollama/models was retired in C1.2b. Callers (CLI / portal)
            // discover Ollama models through the per-provider install path:
            // GET /api/v1/tenant/model-providers/installs/ollama/models.
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

            // OTLP/HTTP+JSON ingest endpoints (#2492). Auth is the
            // OtlpCallbackScheme (per-invocation callback JWT minted by
            // the launcher); the endpoints sit outside the tenant API
            // group because the auth surface is distinct from API tokens.
            app.MapOtlpIngestEndpoints()
                .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(AuthConstants.OtlpCallbackScheme)
                    .RequireAuthenticatedUser()
                    .Build());

            await app.RunAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FATAL: Host.Api failed to start. Exiting with code 1 so the container orchestrator can restart the process.");
            Console.Error.WriteLine(ex.ToString());
            Environment.Exit(1);
        }
    }
}
