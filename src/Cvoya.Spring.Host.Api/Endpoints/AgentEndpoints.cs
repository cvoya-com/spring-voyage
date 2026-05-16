// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Maps agent-related API endpoints.
/// </summary>
public static class AgentEndpoints
{
    /// <summary>
    /// Registers agent endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/agents")
            .WithTags("Agents");

        group.MapGet("/", ListAgentsAsync)
            .WithName("ListAgents")
            .WithSummary("List all registered agents")
            .Produces<AgentResponse[]>(StatusCodes.Status200OK);

        group.MapGet("/{id}", GetAgentAsync)
            .WithName("GetAgent")
            .WithSummary("Get agent status by sending a StatusQuery message")
            .Produces<AgentDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateAgentAsync)
            .WithName("CreateAgent")
            .WithSummary("Create a new agent")
            .Accepts<CreateAgentRequest>("application/json")
            .Produces<AgentResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            // ADR-0039 §6 / plan task B1: multi-parent inheritance conflict.
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPatch("/{id}", UpdateAgentMetadataAsync)
            .WithName("UpdateAgentMetadata")
            .WithSummary("Update the agent's metadata (model, specialty, enabled, execution mode, instructions)")
            // #2293: the handler reads the body via HttpContext so it can
            // distinguish absent-vs-explicit-null on the `instructions`
            // tri-state, so we declare the contract surface via .Accepts.
            .Accepts<UpdateAgentMetadataRequest>("application/json")
            .Produces<AgentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // #2360: equipped skill bundles on an agent. The pre-#2360
        // `/skills` surface returned a flat string[] of tool names
        // (legacy MCP-tool grant shape from the Tools wave); it was
        // superseded by the proper Tools surface under Config → Tools.
        // The new shape under this route is operator-equip of skill
        // bundles, which feeds Layer 4 of the assembled prompt.
        group.MapGet("/{id}/skills", GetEquippedSkillsAsync)
            .WithName("GetAgentSkills")
            .WithSummary("List the skill bundles equipped on an agent")
            .WithDescription("Returns the resolved bundles in declaration order. Each entry carries the package + skill coordinates, a prompt-body summary, and the bundle's required-tool list.")
            .Produces<EquippedSkillsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/skills", EquipAgentSkillAsync)
            .WithName("EquipAgentSkill")
            .WithSummary("Equip a skill bundle on an agent")
            .WithDescription("Idempotent on (packageName, skillName). The store re-resolves the bundle so the persisted record carries the freshest prompt + required-tools snapshot. Returns the new effective list in declaration order.")
            .Produces<EquippedSkillsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id}/skills/{packageName}/{skillName}", UnequipAgentSkillAsync)
            .WithName("UnequipAgentSkill")
            .WithSummary("Unequip a skill bundle from an agent")
            .WithDescription("No-op when the bundle is not currently equipped. Returns the new effective list.")
            .Produces<EquippedSkillsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{id}", DeleteAgentAsync)
            .WithName("DeleteAgent")
            .WithSummary("Delete an agent")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Persistent-agent lifecycle surface (#396). Distinct from ephemeral
        // dispatch — `deploy` stands up the long-lived container, `undeploy`
        // tears it down, `scale` changes replica count (reserved), and `logs`
        // streams the container tail. `delete` above still removes the agent
        // record itself; a persistent agent should be undeployed first so the
        // dangling container is cleaned up.
        group.MapPost("/{id}/deploy", DeployPersistentAgentAsync)
            .WithName("DeployPersistentAgent")
            .WithSummary("Deploy (or reconcile) a persistent agent's backing container")
            .Produces<PersistentAgentDeploymentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id}/undeploy", UndeployPersistentAgentAsync)
            .WithName("UndeployPersistentAgent")
            .WithSummary("Tear down a persistent agent's backing container (idempotent)")
            .Produces<PersistentAgentDeploymentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // #2364: lifecycle-state transitions mirroring the unit surface.
        // Agents now share the Draft → Validating → Stopped → Starting →
        // Running state machine; these endpoints are the operator-driven
        // entry points for manual transitions (the auto-start gate at
        // install time drives the same flow without operator action).
        group.MapPost("/{id}/start", StartAgentAsync)
            .WithName("StartAgent")
            .WithSummary("Start an agent (transitions Stopped → Starting → Running)")
            .Produces<AgentLifecycleResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id}/stop", StopAgentAsync)
            .WithName("StopAgent")
            .WithSummary("Stop a running agent (transitions Running → Stopping → Stopped)")
            .Produces<AgentLifecycleResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id}/revalidate", RevalidateAgentAsync)
            .WithName("RevalidateAgent")
            .WithSummary("Re-run the validation workflow for an agent (Draft/Error/Stopped → Validating). Settles in Stopped on success.")
            .Produces<AgentLifecycleResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id}/scale", ScalePersistentAgentAsync)
            .WithName("ScalePersistentAgent")
            .WithSummary("Adjust replica count for a persistent agent (OSS core supports 0 or 1 today)")
            .Produces<PersistentAgentDeploymentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id}/logs", GetPersistentAgentLogsAsync)
            .WithName("GetPersistentAgentLogs")
            .WithSummary("Read the container logs for a persistent agent")
            .Produces<PersistentAgentLogsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id}/deployment", GetPersistentAgentDeploymentAsync)
            .WithName("GetPersistentAgentDeployment")
            .WithSummary("Get the current deployment state of a persistent agent (container + health)")
            .Produces<PersistentAgentDeploymentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Portal runtime-status indicator (#2100). Cheap polling endpoint
        // backing the per-name status chip. Distinct from
        // /{id} (full StatusQuery via the message router): no activity
        // event, no permission gate beyond the group's authentication,
        // sub-2s latency target. Polled by the portal at ~2s cadence
        // when a chip is on screen.
        group.MapGet("/{id}/runtime-status", GetAgentRuntimeStatusAsync)
            .WithName("GetAgentRuntimeStatus")
            .WithSummary("Get the agent's runtime-status indicator (idle / busy / queued / unavailable)")
            .Produces<AgentRuntimeStatusResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Agent execution surface (#601 / #603 / #409 B-wide). Exposes
        // the agent's own `execution:` block (image / runtime / model /
        // hosting). Resolution chain (agent → unit
        // → fail) happens in IAgentDefinitionProvider at dispatch time.
        group.MapGet("/{id}/execution", GetAgentExecutionAsync)
            .WithName("GetAgentExecution")
            .WithSummary("Get the agent's persisted execution block")
            .Produces<AgentExecutionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // ADR-0039 §9: the PUT handler hand-reads the body as
        // JsonDocument so it can reject the legacy `containerRuntime`
        // key with a structured 400 before model-binding silently drops
        // it. The DTO surface stays advertised via Accepts<T>() so the
        // OpenAPI schema (and downstream Kiota client) keeps a typed
        // body.
        group.MapPut("/{id}/execution", SetAgentExecutionAsync)
            .WithName("SetAgentExecution")
            .WithSummary("Upsert one or more fields on the agent's execution block (partial update)")
            .Accepts<AgentExecutionResponse>("application/json")
            .Produces<AgentExecutionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{id}/execution", ClearAgentExecutionAsync)
            .WithName("ClearAgentExecution")
            .WithSummary("Clear the agent's execution block (strip all fields)")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> GetAgentExecutionAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IAgentExecutionStore store,
        [FromServices] SpringDbContext db,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
        var shape = await store.GetAsync(actorId, cancellationToken);

        // #2096 / ADR-0041: surface execution.concurrent_threads on the
        // wire so `spring agent validate` can warn on opt-in. The
        // IAgentExecutionStore shape doesn't carry the bool — it is
        // read directly from the persisted definition JSON. Failure to
        // peek (no row, malformed JSON, missing slot) leaves the field
        // null, which the CLI treats as "use the runtime default" and
        // does not warn on.
        bool? concurrentThreads = await ReadConcurrentThreadsFromDefinitionAsync(
            db, entry.ActorId, cancellationToken);

        return Results.Ok(ToAgentExecutionResponse(shape, concurrentThreads));
    }

    /// <summary>
    /// Peeks at the persisted <c>AgentDefinitions.Definition</c> document
    /// and reads the <c>execution.concurrent_threads</c> boolean (#2096 /
    /// ADR-0041). Returns <c>null</c> when the agent has no definition row,
    /// the document doesn't carry an <c>execution</c> object, or the
    /// <c>concurrent_threads</c> slot is absent — all three cases mean
    /// "use the runtime's default", which the CLI does not warn on.
    /// </summary>
    private static async Task<bool?> ReadConcurrentThreadsFromDefinitionAsync(
        SpringDbContext db,
        Guid agentActorId,
        CancellationToken cancellationToken)
    {
        var entity = await db.AgentDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                a => a.Id == agentActorId && a.DeletedAt == null,
                cancellationToken);

        if (entity is null || entity.Definition is not { } definition)
        {
            return null;
        }

        if (definition.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!definition.TryGetProperty("execution", out var exec)
            || exec.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!exec.TryGetProperty("concurrent_threads", out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => (bool?)null,
        };
    }

    private static async Task<IResult> SetAgentExecutionAsync(
        string id,
        HttpContext httpContext,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IAgentExecutionStore store,
        [FromServices] IUnitMembershipRepository membershipRepository,
        [FromServices] IExecutionConfigInheritanceResolver inheritanceResolver,
        [FromServices] ITenantContext tenantContext,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        // ADR-0039 §7 / §9: read the body as a JsonDocument first so we
        // can reject the removed `containerRuntime` key with a structured
        // 400 before model-binding silently drops it. Legacy clients
        // still in the field need an actionable error, not a no-op
        // success.
        var jsonOptions = httpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value
            .SerializerOptions;

        AgentExecutionResponse? request;
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(httpContext.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            return Results.Problem(
                detail: $"Request body is not valid JSON: {ex.Message}",
                statusCode: StatusCodes.Status400BadRequest);
        }

        using (document)
        {
            var legacy = LegacyExecutionFieldProblems
                .LegacyContainerRuntimeFieldOrNull(document.RootElement);
            if (legacy is not null)
            {
                return legacy;
            }

            try
            {
                request = document.Deserialize<AgentExecutionResponse>(jsonOptions);
            }
            catch (JsonException ex)
            {
                return Results.Problem(
                    detail: $"Request body could not be deserialized: {ex.Message}",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        request ??= new AgentExecutionResponse();

        // ADR-0038: map the new wire shape onto the internal store shape.
        // ADR-0039 §7/G8 drops the container-runtime slot; the host
        // process owns that platform setting.
        var shape = new AgentExecutionShape(
            Image: request.Image,
            Provider: request.Model?.Provider,
            Model: request.Model?.Id,
            Hosting: request.Hosting,
            Agent: request.Runtime);

        if (shape.IsEmpty)
        {
            return Results.Problem(
                detail: "Execution block must carry at least one non-empty field on PUT. Use DELETE to clear.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Save-time validation (#601 scope item 8): ephemeral hosting
        // with no image anywhere fails at save time, not at dispatch.
        // We check the agent-side declared-or-becoming image here; the
        // IAgentDefinitionProvider merges the unit-level default at
        // dispatch so an agent that declares hosting-ephemeral but no
        // image still starts up cleanly if the unit carries one. We
        // cannot cheaply peek at the unit default here without a second
        // scope call, so we only reject the narrow case where the agent
        // declares ephemeral hosting AND clears the image AND has no
        // image already persisted AND no inherited image — that last
        // check is deferred to the dispatcher (same error message now
        // points operators at both surfaces).
        //
        // The portal / CLI surface the effective-resolution view so the
        // operator sees the merged state before saving; the narrow
        // dispatch-time fall-through keeps the API permissive for
        // programmatic callers that write fields in any order.

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);

        // ADR-0039 §6 (B2): multi-parent inheritance validation runs
        // BEFORE the store.SetAsync call. The "agent's own" view the
        // resolver intersects against parent set is the patched view —
        // patched values from the request layered over whatever is
        // already persisted on the agent. A field the operator left null
        // in the patch keeps its existing persisted value (mirrors the
        // partial-update semantics of DbAgentExecutionStore.SetAsync);
        // a field the operator set explicitly suppresses inheritance for
        // that slot per the resolver's contract.
        var existing = await store.GetAsync(actorId, cancellationToken);
        var merged = MergePatchedShape(existing, shape);
        var memberships = await membershipRepository.ListByAgentAsync(entry.ActorId, cancellationToken);
        var parentUnitIds = memberships.Select(m => m.UnitId).ToList();
        var resolution = inheritanceResolver.ResolveAgentConfig(
            agentOwn: ShapeToConfig(merged),
            parentUnitIds: parentUnitIds,
            tenantId: tenantContext.CurrentTenantId,
            ct: cancellationToken);
        if (resolution.ConflictingFields.Count > 0)
        {
            return MultiParentInheritanceConflictResult(resolution.ConflictingFields);
        }

        await store.SetAsync(actorId, shape, cancellationToken);
        var stored = await store.GetAsync(actorId, cancellationToken);
        return Results.Ok(ToAgentExecutionResponse(stored));
    }

    /// <summary>
    /// Layers <paramref name="patch"/> over <paramref name="existing"/> using
    /// the same partial-update semantics as
    /// <see cref="DbAgentExecutionStore"/>.<c>SetAsync</c>: a non-blank field on
    /// the patch replaces the existing slot; a null/blank field leaves the
    /// existing value alone. Used by the <c>PUT /api/v1/tenant/agents/{id}/execution</c>
    /// path so the inheritance resolver sees the post-patch agent config (ADR-0039 §6 / B2).
    /// </summary>
    private static AgentExecutionShape MergePatchedShape(
        AgentExecutionShape? existing,
        AgentExecutionShape patch)
    {
        existing ??= new AgentExecutionShape();
        return new AgentExecutionShape(
            Image: PickNonBlank(patch.Image, existing.Image),
            Provider: PickNonBlank(patch.Provider, existing.Provider),
            Model: PickNonBlank(patch.Model, existing.Model),
            Hosting: PickNonBlank(patch.Hosting, existing.Hosting),
            Agent: PickNonBlank(patch.Agent, existing.Agent));

        static string? PickNonBlank(string? incoming, string? fallback)
        {
            if (!string.IsNullOrWhiteSpace(incoming)) return incoming.Trim();
            if (!string.IsNullOrWhiteSpace(fallback)) return fallback.Trim();
            return null;
        }
    }

    /// <summary>
    /// Projects an <see cref="AgentExecutionShape"/> onto an
    /// <see cref="AgentExecutionConfig"/> for the inheritance resolver. The
    /// resolver intersects against parent unit defaults per field; a blank
    /// slot on the agent surfaces the field as a candidate for inheritance.
    /// </summary>
    /// <remarks>
    /// <see cref="AgentExecutionConfig.AgentRuntimeId"/> is non-nullable so a
    /// missing <see cref="AgentExecutionShape.Agent"/> projects to the empty
    /// string — the resolver treats blank as null-equivalent through its own
    /// <c>NullIfBlank</c> normaliser, so the candidate-for-inheritance branch
    /// fires the same way. <c>hosting</c> on the shape is a free-form string
    /// (<c>"ephemeral"</c> / <c>"persistent"</c>); the resolver passes
    /// <see cref="AgentExecutionConfig.Hosting"/> through verbatim because
    /// hosting is agent-owned per ADR-0039 §6 — the parsed enum value here
    /// only affects the round-trip through the resolver and is not what the
    /// store persists.
    /// </remarks>
    private static AgentExecutionConfig ShapeToConfig(AgentExecutionShape shape) =>
        new(
            AgentRuntimeId: shape.Agent ?? string.Empty,
            Image: shape.Image,
            Hosting: ParseHostingMode(shape.Hosting),
            Provider: shape.Provider,
            Model: shape.Model);

    private static AgentHostingMode ParseHostingMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AgentHostingMode.Persistent;
        }
        if (value.Equals("persistent", StringComparison.OrdinalIgnoreCase))
        {
            return AgentHostingMode.Persistent;
        }
        if (value.Equals("ephemeral", StringComparison.OrdinalIgnoreCase))
        {
            return AgentHostingMode.Ephemeral;
        }
        if (value.Equals("pooled", StringComparison.OrdinalIgnoreCase))
        {
            return AgentHostingMode.Pooled;
        }
        return AgentHostingMode.Persistent;
    }

    /// <summary>
    /// Builds the structured 422 envelope ADR-0039 §6 pins for a
    /// multi-parent inheritance conflict. Mirrors the body shape the
    /// CLI / portal pattern-match on:
    /// <c>{ "error": "MultiParentInheritanceConflict", "conflictingFields": { … } }</c>.
    /// </summary>
    /// <remarks>
    /// Each entry under <c>conflictingFields</c> is the diverging field name
    /// (e.g. <c>image</c>, <c>agent</c>, <c>model</c>) mapped to the list
    /// of contributing parent values. Each parent value is a
    /// <c>{ source, value }</c> pair where <c>source</c> is the parent unit
    /// id rendered in 32-char no-dash hex per the platform's URL/wire form
    /// for actor identifiers (CONVENTIONS.md § Identifiers).
    /// </remarks>
    private static IResult MultiParentInheritanceConflictResult(
        IReadOnlyDictionary<string, IReadOnlyList<ParentValue>> conflictingFields)
    {
        var serialised = conflictingFields.ToDictionary(
            kvp => kvp.Key,
            kvp => (object?)kvp.Value
                .Select(pv => new
                {
                    source = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(pv.Source),
                    value = pv.Value,
                })
                .ToArray());

        return Results.Json(
            new
            {
                error = "MultiParentInheritanceConflict",
                conflictingFields = serialised,
            },
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ClearAgentExecutionAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IAgentExecutionStore store,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
        await store.ClearAsync(actorId, cancellationToken);
        return Results.NoContent();
    }

    internal static AgentExecutionResponse ToAgentExecutionResponse(
        AgentExecutionShape? shape,
        bool? concurrentThreads = null)
    {
        if (shape is null)
        {
            // Even a missing shape carries through `concurrentThreads` —
            // an agent definition can declare `execution.concurrent_threads`
            // without declaring image / runtime / model / hosting, and the
            // CLI's validate verb still needs to see the flag.
            return new AgentExecutionResponse(ConcurrentThreads: concurrentThreads);
        }

        // ADR-0038: project the internal store shape onto the new wire
        // form — `runtime` (catalogue id), structured `model: {provider, id}`,
        // and `hosting`. ADR-0039 §7 drops the wire-side `containerRuntime`
        // slot; the internal store still carries it for back-compat (until
        // G8) but it is never emitted on the wire.
        AiModelDto? model = null;
        if (!string.IsNullOrWhiteSpace(shape.Model) && !string.IsNullOrWhiteSpace(shape.Provider))
        {
            model = new AiModelDto(shape.Provider!, shape.Model!);
        }

        return new AgentExecutionResponse(
            Image: shape.Image,
            Runtime: shape.Agent,
            Model: model,
            Hosting: shape.Hosting,
            ConcurrentThreads: concurrentThreads);
    }

    private static async Task<IResult> DeployPersistentAgentAsync(
        string id,
        DeployPersistentAgentRequest? request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] PersistentAgentLifecycle lifecycle,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        // The OSS core only supports replicas ∈ {0, 1} today. We accept a
        // nullable int on the wire so the default (and most callers) don't
        // need to send a body at all.
        var replicas = request?.Replicas ?? 1;
        if (replicas < 0 || replicas > 1)
        {
            return Results.Problem(
                detail: "Only replicas in {0, 1} are supported by the OSS core; horizontal scaling is a tracked follow-up.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // #1748: lifecycle / registry are keyed by the agent's actor Guid.
        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);

        try
        {
            if (replicas == 0)
            {
                // Scale-to-zero intent: the caller asked to deploy with 0
                // replicas. Treat as undeploy and return the canonical empty
                // shape so CLIs see a consistent wire contract.
                await lifecycle.UndeployAsync(actorId, cancellationToken);
                // The wire shape preserves the URL-path agent id so callers
                // see a stable AgentId across the request/response pair, even
                // when (in test fixtures) the directory entry's ActorId is
                // not the same Guid as the route segment.
                return Results.Ok(EmptyDeploymentResponse(id, replicas: 0));
            }

            var deployed = await lifecycle.DeployAsync(
                actorId,
                imageOverride: request?.Image,
                cancellationToken);
            return Results.Ok(ToDeploymentResponse(deployed, replicas: 1));
        }
        catch (SpringException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> UndeployPersistentAgentAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] PersistentAgentLifecycle lifecycle,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
        await lifecycle.UndeployAsync(actorId, cancellationToken);

        // Always return the canonical "not running" shape so the CLI can
        // treat the response the same whether the agent was running or not.
        return Results.Ok(EmptyDeploymentResponse(id, replicas: 0));
    }

    private static async Task<IResult> ScalePersistentAgentAsync(
        string id,
        ScalePersistentAgentRequest request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] PersistentAgentLifecycle lifecycle,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);

        try
        {
            var scaled = await lifecycle.ScaleAsync(actorId, request.Replicas, cancellationToken);
            return Results.Ok(ToDeploymentResponse(scaled, request.Replicas));
        }
        catch (SpringException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> GetPersistentAgentLogsAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] PersistentAgentLifecycle lifecycle,
        [FromServices] PersistentAgentRegistry registry,
        [FromQuery] int? tail,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
        var effectiveTail = tail is > 0 ? tail.Value : 200;

        try
        {
            var logs = await lifecycle.GetLogsAsync(actorId, effectiveTail, cancellationToken);
            registry.TryGet(actorId, out var registered);
            return Results.Ok(new PersistentAgentLogsResponse(
                AgentId: id,
                ContainerId: registered?.ContainerId ?? string.Empty,
                Tail: effectiveTail,
                Logs: logs));
        }
        catch (SpringException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
    }

    private static async Task<IResult> GetPersistentAgentDeploymentAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] PersistentAgentRegistry registry,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
        if (registry.TryGet(actorId, out var deployment) && deployment is not null)
        {
            return Results.Ok(ToDeploymentResponse(deployment, replicas: 1));
        }

        return Results.Ok(EmptyDeploymentResponse(id, replicas: 0));
    }

    /// <summary>
    /// Returns the runtime-status indicator for an agent (#2100). Combines
    /// the actor's per-thread channel snapshot
    /// (<see cref="IAgentActor.GetRuntimeStatusAsync"/>) with the
    /// <see cref="PersistentAgentRegistry"/> health probe to project one
    /// of <c>idle</c> / <c>busy</c> / <c>queued</c> / <c>unavailable</c>.
    /// Endpoint is poll-friendly: no activity-event side effect, no
    /// permission gate beyond the group-level authentication.
    /// </summary>
    private static async Task<IResult> GetAgentRuntimeStatusAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] PersistentAgentRegistry persistentAgentRegistry,
        [FromServices] IAgentExecutionStore executionStore,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);

        // Persistent-mode agents only: an `unavailable` probe value can
        // come from the registry — ephemeral agents have no container to
        // be unhealthy. Look up the agent's hosting mode through the
        // execution store; on transient store error fall through to the
        // actor read (fail-open: report idle/busy/queued rather than
        // misclassify a healthy ephemeral agent as unavailable).
        AgentExecutionShape? shape = null;
        try
        {
            shape = await executionStore.GetAsync(actorId, cancellationToken);
        }
        catch
        {
            // Fail-open per the comment above.
        }

        var isPersistent = string.Equals(shape?.Hosting, "persistent", StringComparison.OrdinalIgnoreCase);
        if (isPersistent)
        {
            // Persistent agent: registry MUST carry an entry once the
            // agent has been deployed. No entry => not deployed yet =>
            // unavailable. Entry present but Unhealthy => unavailable.
            if (!persistentAgentRegistry.TryGet(actorId, out var registryEntry) || registryEntry is null)
            {
                return Results.Ok(new AgentRuntimeStatusResponse(
                    Status: "unavailable",
                    LastUpdated: DateTimeOffset.UtcNow,
                    InFlightThreadCount: 0,
                    QueuedMessageCount: 0));
            }

            if (registryEntry.HealthStatus != AgentHealthStatus.Healthy)
            {
                return Results.Ok(new AgentRuntimeStatusResponse(
                    Status: "unavailable",
                    LastUpdated: DateTimeOffset.UtcNow,
                    InFlightThreadCount: 0,
                    QueuedMessageCount: 0));
            }
        }

        // Healthy / ephemeral path: ask the actor for its per-thread
        // channel snapshot. Wrap the proxy call so a transient Dapr
        // hiccup surfaces as `idle` rather than 5xx — the chip is a
        // best-effort indicator and a stale-but-rendered chip is a
        // strictly better failure mode than a blanked card.
        AgentRuntimeStatusReport? report = null;
        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
                new ActorId(actorId), nameof(AgentActor));
            report = await proxy.GetRuntimeStatusAsync(cancellationToken);
        }
        catch (Exception)
        {
            // swallow — best-effort indicator, idle is safer than 5xx
        }

        report ??= new AgentRuntimeStatusReport(
            InFlightThreadCount: 0,
            QueuedMessageCount: 0,
            ChannelCount: 0,
            ObservedAt: DateTimeOffset.UtcNow);

        return Results.Ok(ProjectRuntimeStatus(report));
    }

    /// <summary>
    /// Maps an actor-side <see cref="AgentRuntimeStatusReport"/> to the
    /// portal-facing <see cref="AgentRuntimeStatusResponse"/>. Pure;
    /// shared with the unit endpoint variant.
    /// </summary>
    internal static AgentRuntimeStatusResponse ProjectRuntimeStatus(AgentRuntimeStatusReport report)
    {
        // busy strictly wins over queued: an in-flight dispatcher is the
        // signal humans need to see ("the agent is doing something
        // somewhere right now"). queued without busy is the head-of-line
        // signal — the actor has nothing actively dispatching but
        // messages are sitting in a channel between drains.
        var status = (report.InFlightThreadCount, report.QueuedMessageCount) switch
        {
            ( > 0, _) => "busy",
            (0, > 0) => "queued",
            _ => "idle",
        };

        return new AgentRuntimeStatusResponse(
            Status: status,
            LastUpdated: report.ObservedAt,
            InFlightThreadCount: report.InFlightThreadCount,
            QueuedMessageCount: report.QueuedMessageCount);
    }

    /// <summary>
    /// Canonical "running" wire shape for a persistent deployment. Maps the
    /// registry's <see cref="PersistentAgentEntry"/> to the HTTP response so
    /// callers never have to reach into the registry types directly.
    /// </summary>
    internal static PersistentAgentDeploymentResponse ToDeploymentResponse(
        PersistentAgentEntry entry,
        int replicas) =>
        new(
            AgentId: entry.AgentId,
            Running: entry.ContainerId is not null,
            HealthStatus: entry.HealthStatus switch
            {
                AgentHealthStatus.Healthy => "healthy",
                AgentHealthStatus.Unhealthy => "unhealthy",
                _ => "unknown",
            },
            Replicas: replicas,
            Image: entry.Definition?.Execution?.Image,
            Endpoint: entry.Endpoint?.ToString(),
            ContainerId: entry.ContainerId,
            StartedAt: entry.StartedAt,
            ConsecutiveFailures: entry.ConsecutiveFailures);

    /// <summary>
    /// Canonical "not running" shape. Returned when there is no entry in the
    /// registry (never deployed, or already undeployed).
    /// </summary>
    internal static PersistentAgentDeploymentResponse EmptyDeploymentResponse(string agentId, int replicas) =>
        new(
            AgentId: agentId,
            Running: false,
            HealthStatus: "unknown",
            Replicas: replicas,
            Image: null,
            Endpoint: null,
            ContainerId: null,
            StartedAt: null,
            ConsecutiveFailures: 0);

    private static async Task<IResult> ListAgentsAsync(
        IDirectoryService directoryService,
        [FromServices] IAgentExecutionStore executionStore,
        [FromServices] IInitiativeEngine initiativeEngine,
        [FromServices] IUnitMembershipRepository membershipRepository,
        [FromQuery] string? hosting,
        [FromQuery(Name = "initiative")] string[]? initiative,
        [FromQuery(Name = "display_name")] string? displayName,
        [FromQuery(Name = "unit_id")] string? unitId,
        CancellationToken cancellationToken)
    {
        var entries = await directoryService.ListAllAsync(cancellationToken);

        // Intentionally does NOT enrich with actor metadata — the list
        // endpoint is a cheap directory scan and callers who need per-agent
        // metadata use GET /api/v1/agents/{id} or the unit-scoped list.
        // Response fields below RegisteredAt fall back to defaults (see
        // ToAgentResponse).
        //
        // #572 / #573: hosting mode and initiative level are read from the
        // execution store and initiative engine respectively. Both are
        // local DB reads (no actor fan-out). We fire them in parallel per
        // agent and fail-open (null) when either call throws so a transient
        // outage never blanks the whole list.
        var agentEntries = entries
            .Where(e => string.Equals(e.Address.Scheme, "agent", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // #1649: server-side display_name + unit_id filtering. Applied
        // BEFORE enrichment so the cheap directory equality check eliminates
        // most candidates before the per-agent execution-store / initiative
        // fan-out fires. The CLI resolver (PR #1650) used to list+filter
        // client-side; with these filters in place the resolver collapses
        // to a single round-trip per name lookup.
        //
        // Filtering rules per the issue:
        // - display_name match is case-insensitive equality (substring /
        //   fuzzy is an explicit follow-up, not in scope here).
        // - unit_id constrains the result to agents whose membership row
        //   names that unit. We accept both no-dash and dashed Guid forms
        //   so the CLI's lenient parse round-trips. A malformed unit_id is
        //   silently treated as "no match" rather than 400, mirroring the
        //   directory's "not found returns empty" stance — the CLI never
        //   sends a malformed unit_id since it parses with the same
        //   GuidFormatter, and a 400 here would force every caller to
        //   thread a no-match branch.
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            var trimmed = displayName.Trim();
            agentEntries = agentEntries
                .Where(e => string.Equals(e.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(unitId))
        {
            if (Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(unitId, out var unitGuid))
            {
                var memberships = await membershipRepository.ListByUnitAsync(unitGuid, cancellationToken);
                var memberAgentIds = new HashSet<Guid>(memberships.Select(m => m.AgentId));
                agentEntries = agentEntries
                    .Where(e => memberAgentIds.Contains(e.ActorId))
                    .ToList();
            }
            else
            {
                // Unparseable unit_id ⇒ no agents can satisfy the filter.
                // Empty array is the canonical "no match" wire shape.
                agentEntries = new List<DirectoryEntry>();
            }
        }

        var enrichmentTasks = agentEntries.Select(async e =>
        {
            var agentId = e.Address.Path;

            AgentExecutionShape? shape = null;
            try
            {
                shape = await executionStore.GetAsync(agentId, cancellationToken);
            }
            catch
            {
                // Fail-open: hosting mode stays null on transient error.
            }

            InitiativeLevel? level = null;
            try
            {
                level = await initiativeEngine.GetCurrentLevelAsync(agentId, cancellationToken);
            }
            catch
            {
                // Fail-open: initiative level stays null on transient error.
            }

            // #2250: surface the primary parent unit Guid so the portal /
            // CLI can fetch unit-scoped resources without a slug→guid
            // round trip. Fail-open (null) — list view stays usable if
            // the membership table is briefly unreachable.
            Guid? parentUnitGuid = null;
            try
            {
                var memberships = await membershipRepository.ListByAgentAsync(e.ActorId, cancellationToken);
                if (memberships.Count > 0)
                {
                    parentUnitGuid = memberships[0].UnitId;
                }
            }
            catch
            {
                // Fail-open: parent-unit pointer stays null on transient error.
            }

            return ToAgentResponse(
                e,
                hostingMode: shape?.Hosting,
                initiativeLevel: level,
                parentUnitId: parentUnitGuid);
        });

        var agents = await Task.WhenAll(enrichmentTasks);

        // #1402: server-side filtering by hosting mode and initiative level.
        // Applied after enrichment so the filter sees the fully-populated fields.
        // ?hosting=ephemeral|persistent — single-value (one mode at a time).
        // ?initiative=passive&initiative=autonomous — multi-value (repeated param).
        var filtered = (IEnumerable<AgentResponse>)agents;

        if (!string.IsNullOrWhiteSpace(hosting))
        {
            var hostingNorm = hosting.Trim().ToLowerInvariant();
            filtered = filtered.Where(a =>
                string.Equals(a.HostingMode, hostingNorm, StringComparison.OrdinalIgnoreCase));
        }

        if (initiative is { Length: > 0 })
        {
            // Normalise every value to lower-case for case-insensitive matching.
            // Comma-separated values within a single param occurrence are also
            // supported (e.g. ?initiative=proactive,autonomous) for curl convenience.
            var initiativeSet = initiative
                .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(v => v.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            filtered = filtered.Where(a =>
                a.InitiativeLevel is not null && initiativeSet.Contains(a.InitiativeLevel));
        }

        return Results.Ok(filtered.ToArray());
    }

    private static async Task<IResult> GetAgentAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IUnitMembershipRepository membershipRepository,
        [FromServices] MessageRouter messageRouter,
        [FromServices] IAuthenticatedCallerAccessor callerAccessor,
        [FromServices] PersistentAgentRegistry persistentAgentRegistry,
        [FromServices] IAgentExecutionStore executionStore,
        [FromServices] IInitiativeEngine initiativeEngine,
        [FromServices] IToolGrantResolver toolGrantResolver,
        [FromServices] IAgentDefinitionProvider agentDefinitionProvider,
        [FromServices] IServiceScopeFactory scopeFactory,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var address = Address.For("agent", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId)), nameof(AgentActor));
        var agentActorUuid = entry.ActorId;
        var (metadata, parentUnitGuid) = await GetDerivedAgentMetadataAsync(proxy, membershipRepository, agentActorUuid, directoryService, cancellationToken);

        // #339: Thread the authenticated caller's identity through as the
        // From address rather than hardcoding `human://api`. The router's
        // permission gate only fires for `unit://` destinations today, so
        // `agent://` dispatch works either way — but the synthetic identity
        // dropped observability (activity events are labelled with the
        // sender) and masked auth bugs. Falls back to `human://api` only
        // when no authenticated principal is present.
        var statusQuery = new Message(
            Guid.NewGuid(),
            await callerAccessor.GetCallerAddressAsync(cancellationToken),
            address,
            MessageType.StatusQuery,
            null,
            default,
            DateTimeOffset.UtcNow);

        var result = await messageRouter.RouteAsync(statusQuery, cancellationToken);

        // #1748: registry / stores below are all keyed by the agent's
        // actor Guid (the form PersistentAgentLifecycle.DeployAsync registers
        // with and DbAgentExecutionStore parses).
        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);

        // Persistent-agent health enrichment (#396): when a persistent
        // deployment is tracked, surface it alongside the actor's status
        // payload so `spring agent status <id>` is a single stop for both
        // ephemeral-actor state and persistent-container state.
        PersistentAgentDeploymentResponse? deployment = null;
        if (persistentAgentRegistry.TryGet(actorId, out var persistentEntry) && persistentEntry is not null)
        {
            deployment = ToDeploymentResponse(persistentEntry, replicas: 1);
        }

        // #572 / #573: populate hosting mode and initiative level on the
        // detail response. Fail-open (null) so a transient outage from
        // either store doesn't block the status verb entirely.
        AgentExecutionShape? shape = null;
        try
        {
            shape = await executionStore.GetAsync(actorId, cancellationToken);
        }
        catch
        {
            // Fail-open: hosting mode stays null.
        }

        InitiativeLevel? level = null;
        try
        {
            level = await initiativeEngine.GetCurrentLevelAsync(actorId, cancellationToken);
        }
        catch
        {
            // Fail-open: initiative level stays null.
        }

        // #2156: surface the agent's installation-lifecycle row on the
        // detail response so the UI / CLI can distinguish "installed
        // cleanly" from "install failed; here's why" without grepping
        // worker logs. Fail-open (null) so a transient actor outage
        // doesn't blank the otherwise-complete response.
        LifecycleStatus? lifecycleStatus = null;
        string? lifecycleError = null;
        try
        {
            lifecycleStatus = await proxy.GetStatusAsync(cancellationToken);
            if (lifecycleStatus == LifecycleStatus.Error)
            {
                lifecycleError = await proxy.GetLifecycleErrorAsync(cancellationToken);
            }
        }
        catch
        {
            // Fail-open: lifecycle stays null.
        }

        // #2293: surface the persisted `instructions` slot from the agent
        // definition so the portal's Instructions sub-tab and the CLI can
        // see the agent's own value without a follow-up call. Fail-open
        // (null) — the dispatcher always re-reads at dispatch time.
        var instructions = await TryReadAgentInstructionsAsync(
            scopeFactory,
            entry.ActorId,
            loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.AgentEndpoints"),
            id,
            cancellationToken);

        // #2337 Sub D: resolve effective tools so the portal's Tools
        // sub-tab can render the three-tier layout. Fail-open (empty
        // list) — a transient resolver outage shouldn't blank the
        // otherwise-complete response.
        var effectiveTools = await TryResolveEffectiveToolsAsync(
            toolGrantResolver,
            new Address(Address.AgentScheme, entry.ActorId),
            loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.AgentEndpoints"),
            id,
            cancellationToken);

        // #2348: surface the agent's effective execution.image tag through
        // IAgentDefinitionProvider — the same path the dispatcher uses, so
        // the agent→parent-unit merge (#601 / #603) flows through
        // automatically. Fail-open (null) — a transient definition-store
        // outage shouldn't blank the otherwise-complete response.
        var executionImage = await TryResolveExecutionImageAsync(
            agentDefinitionProvider,
            actorId,
            loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.AgentEndpoints"),
            id,
            cancellationToken);

        var agentResponse = ToAgentResponse(
            entry,
            metadata,
            hostingMode: shape?.Hosting,
            initiativeLevel: level,
            lifecycleStatus: lifecycleStatus,
            lifecycleError: lifecycleError,
            parentUnitId: parentUnitGuid,
            instructions: instructions,
            effectiveTools: effectiveTools,
            executionImage: executionImage);
        if (!result.IsSuccess)
        {
            return Results.Ok(new AgentDetailResponse(agentResponse, null, deployment));
        }

        // #1000: serialise the actor's opaque status payload as a JSON string on the
        // wire so the Kiota client sees a flat `string?` rather than the empty-schema
        // oneOf that Kiota cannot round-trip. JsonElement.ValueKind == Undefined means
        // "no payload"; treat that the same as null to avoid emitting the literal "null".
        var statusJson = result.Value?.Payload is JsonElement status && status.ValueKind != JsonValueKind.Undefined
            ? status.GetRawText()
            : null;

        return Results.Ok(new AgentDetailResponse(agentResponse, statusJson, deployment));
    }

    private static async Task<IResult> UpdateAgentMetadataAsync(
        string id,
        HttpContext httpContext,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] IUnitMembershipRepository membershipRepository,
        [FromServices] IServiceScopeFactory scopeFactory,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.AgentEndpoints");

        // #2293: read the body twice — once typed (for the existing
        // metadata fields), once raw (so the `instructions` tri-state can
        // distinguish absent from explicit-null on the wire). Mirrors the
        // shape used by UnitEndpoints.UpdateUnitAsync.
        var jsonOptions = httpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value
            .SerializerOptions;

        UpdateAgentMetadataRequest? request;
        AgentInstructionsPatch instructionsPatch;
        try
        {
            using var document = await JsonDocument.ParseAsync(
                httpContext.Request.Body, cancellationToken: cancellationToken);
            request = document.RootElement.Deserialize<UpdateAgentMetadataRequest>(jsonOptions);
            instructionsPatch = ReadAgentInstructionsPatch(document.RootElement);
        }
        catch (JsonException ex)
        {
            return Results.Problem(
                detail: $"Request body is not valid JSON: {ex.Message}",
                statusCode: StatusCodes.Status400BadRequest);
        }

        request ??= new UpdateAgentMetadataRequest();

        // Validate only when the caller actually supplied a new display name —
        // null means "leave unchanged" on the PATCH surface and must stay a no-op
        // for the validator. Mirrors the gate UnitEndpoints.UpdateUnitAsync uses.
        if (request.DisplayName is not null)
        {
            var displayNameProblem = DisplayNameProblems.ValidateOrProblem(request.DisplayName);
            if (displayNameProblem is not null)
            {
                return displayNameProblem;
            }
        }

        var address = Address.For("agent", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        // DisplayName / Description / Role live on the directory entity (same split
        // UnitEndpoints uses for unit metadata). Route those through IDirectoryService
        // so the in-memory cache, role index, and AgentDefinitions row stay consistent.
        if (request.DisplayName is not null || request.Description is not null || request.Role is not null)
        {
            var updatedEntry = await directoryService.UpdateEntryAsync(
                address, request.DisplayName, request.Description, request.Role, cancellationToken);

            entry = updatedEntry ?? entry;
        }

        // ParentUnit is intentionally not accepted here — changing containment
        // must go through the unit's assign / unassign endpoints so the
        // agent.ParentUnit ↔ unit.Members invariant stays consistent.
        var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId)), nameof(AgentActor));

        await proxy.SetMetadataAsync(
            new AgentMetadata(
                Model: request.Model,
                Specialty: request.Specialty,
                Enabled: request.Enabled,
                ExecutionMode: request.ExecutionMode,
                ParentUnit: null),
            cancellationToken);

        // #2293: read-modify-write the agent's persisted Definition JSON
        // when the caller addressed the `instructions` slot. Preserves
        // every sibling property (mirrors UnitCreationService's expertise
        // precedent).
        if (instructionsPatch.IsPresent)
        {
            await ApplyAgentInstructionsPatchAsync(
                scopeFactory, entry.ActorId, logger, id, instructionsPatch.Value, cancellationToken);
        }

        var updated = await proxy.GetMetadataAsync(cancellationToken);
        // #2250: carry the membership-derived primary unit Guid through so
        // the patched response shape matches the GET path.
        var memberships = await membershipRepository.ListByAgentAsync(entry.ActorId, cancellationToken);
        Guid? parentUnitGuid = memberships.Count > 0 ? memberships[0].UnitId : null;
        var instructions = await TryReadAgentInstructionsAsync(
            scopeFactory, entry.ActorId, logger, id, cancellationToken);
        return Results.Ok(ToAgentResponse(entry, updated, parentUnitId: parentUnitGuid, instructions: instructions));
    }

    /// <summary>
    /// Tri-state for the optional <c>instructions</c> slot on PATCH bodies.
    /// </summary>
    private readonly record struct AgentInstructionsPatch(bool IsPresent, string? Value)
    {
        public static AgentInstructionsPatch Absent => new(false, null);
    }

    /// <summary>
    /// Inspects the raw PATCH body for the <c>instructions</c> property
    /// and projects it to the tri-state used by the endpoint
    /// (set / clear / leave-alone).
    /// </summary>
    private static AgentInstructionsPatch ReadAgentInstructionsPatch(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return AgentInstructionsPatch.Absent;
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (!string.Equals(prop.Name, "instructions", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return prop.Value.ValueKind switch
            {
                JsonValueKind.Null => new AgentInstructionsPatch(true, null),
                JsonValueKind.String => new AgentInstructionsPatch(true, prop.Value.GetString()),
                _ => AgentInstructionsPatch.Absent,
            };
        }

        return AgentInstructionsPatch.Absent;
    }

    /// <summary>
    /// Applies the <c>instructions</c> tri-state to the agent's persisted
    /// <c>AgentDefinitions.Definition</c> column. <c>null</c> removes the
    /// key; a string replaces it. Every sibling property is preserved.
    /// </summary>
    private static async Task ApplyAgentInstructionsPatchAsync(
        IServiceScopeFactory scopeFactory,
        Guid agentActorId,
        Microsoft.Extensions.Logging.ILogger logger,
        string agentId,
        string? value,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

            var entity = await db.AgentDefinitions
                .FirstOrDefaultAsync(a => a.Id == agentActorId && a.DeletedAt == null, cancellationToken);

            if (entity is null)
            {
                logger.LogWarning(
                    "Agent '{AgentId}': no AgentDefinition row found while applying instructions patch; skipping.",
                    agentId);
                return;
            }

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (entity.Definition is { ValueKind: JsonValueKind.Object } existing)
            {
                foreach (var prop in existing.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "instructions", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    payload[prop.Name] = prop.Value;
                }
            }

            if (value is not null)
            {
                payload["instructions"] = value;
            }

            entity.Definition = JsonSerializer.SerializeToElement(payload);
            entity.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Agent '{AgentId}': failed to apply instructions patch on AgentDefinition.",
                agentId);
        }
    }

    /// <summary>
    /// Reads the agent's persisted <c>instructions</c> string from the
    /// <c>AgentDefinitions.Definition</c> JSON. Returns <c>null</c> when
    /// the row, the document, or the property is missing.
    /// </summary>
    internal static async Task<string?> TryReadAgentInstructionsAsync(
        IServiceScopeFactory scopeFactory,
        Guid agentActorId,
        Microsoft.Extensions.Logging.ILogger logger,
        string agentId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

            var entity = await db.AgentDefinitions
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == agentActorId && a.DeletedAt == null, cancellationToken);

            if (entity?.Definition is { ValueKind: JsonValueKind.Object } definition
                && definition.TryGetProperty("instructions", out var prop)
                && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Agent '{AgentId}': failed to read instructions from AgentDefinition.",
                agentId);
            return null;
        }
    }

    private static async Task<IResult> CreateAgentAsync(
        HttpContext httpContext,
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        IUnitMembershipRepository membershipRepository,
        IUnitMembershipTenantGuard tenantGuard,
        IExecutionConfigInheritanceResolver inheritanceResolver,
        ITenantContext tenantContext,
        SpringDbContext db,
        CancellationToken cancellationToken)
    {
        var jsonOptions = httpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value
            .SerializerOptions;

        CreateAgentRequest? request;
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(httpContext.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            return Results.Problem(
                detail: $"Request body is not valid JSON: {ex.Message}",
                statusCode: StatusCodes.Status400BadRequest);
        }

        using (document)
        {
            // ADR-0039 §7 / §9: reject the legacy top-level
            // `containerRuntime` key before typed deserialisation silently
            // drops it from the create request DTO.
            var legacy = LegacyExecutionFieldProblems
                .LegacyContainerRuntimeFieldOrNull(document.RootElement);
            if (legacy is not null)
            {
                return legacy;
            }

            try
            {
                request = document.Deserialize<CreateAgentRequest>(jsonOptions);
            }
            catch (JsonException ex)
            {
                return Results.Problem(
                    detail: $"Request body could not be deserialized: {ex.Message}",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        if (request is null)
        {
            return Results.Problem(
                detail: "Request body is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // #1632: a Guid-shaped display name would collide with the Guid-first
        // addressing surface defined by #1629 — every endpoint that accepts
        // a display_name on create / update routes through DisplayNameValidator
        // to keep the rejection class uniform. The validator also catches the
        // empty-string failure mode that used to live inline above.
        var displayNameProblem = DisplayNameProblems.ValidateOrProblem(request.DisplayName);
        if (displayNameProblem is not null)
        {
            return displayNameProblem;
        }

        // ADR-0039 L7: UnitIds is optional. An empty list creates a top-level
        // tenant-parented agent; one or more ids create unit memberships.
        // Post-#1629 every entry is a Guid (the unit's stable actor id).
        var unitIds = (request.UnitIds ?? Array.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        // Resolve every referenced unit BEFORE we touch any server-side
        // state so the caller sees a clean 404 with no partial-register
        // rollback. The first missing unit wins; the error message names
        // it so the caller can correct the request. Per #745 we also
        // require each unit to live in the caller's tenant — the
        // tenant guard rejects cross-tenant unit ids with the same 404
        // shape we return for genuinely missing units, so the agent
        // creation surface never leaks the existence of other-tenant
        // units.
        var resolvedUnits = new List<(Guid Id, DirectoryEntry Entry)>(unitIds.Count);
        foreach (var unitId in unitIds)
        {
            var unitAddress = Address.ForIdentity("unit", unitId);
            var unitEntry = await directoryService.ResolveAsync(unitAddress, cancellationToken);
            if (unitEntry is null)
            {
                return Results.Problem(
                    detail: $"Unit '{Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unitId)}' not found",
                    statusCode: StatusCodes.Status404NotFound);
            }
            // Ask the guard whether the unit is visible in the current
            // tenant. The guard's ShareTenantAsync(unit, agent) needs both
            // addresses to belong to visible rows — at create time the
            // agent row does not exist yet, so we check unit-only
            // visibility by asking "does this unit share a tenant with
            // itself in my scope?".
            var visibleInTenant = await tenantGuard.ShareTenantAsync(
                unitAddress, unitAddress, cancellationToken);
            if (!visibleInTenant)
            {
                return Results.Problem(
                    detail: $"Unit '{Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unitId)}' not found",
                    statusCode: StatusCodes.Status404NotFound);
            }
            resolvedUnits.Add((unitId, unitEntry));
        }

        // Validate the optional definition JSON before we register so a
        // malformed document is rejected without a rollback dance.
        JsonElement? definition = null;
        if (!string.IsNullOrWhiteSpace(request.DefinitionJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(request.DefinitionJson);
                // ADR-0039 §7 / §9: reject the legacy
                // `execution.containerRuntime` key on the agent-definition
                // document. The container runtime is platform configuration
                // — operators surface this on the host config, never on a
                // per-agent definition.
                var legacy = LegacyExecutionFieldProblems
                    .LegacyContainerRuntimeFieldInDefinition(doc.RootElement);
                if (legacy is not null)
                {
                    return legacy;
                }
                definition = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                return Results.Problem(
                    detail: $"DefinitionJson is not valid JSON: {ex.Message}",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        // ADR-0039 §6 / plan task B1: enforce the multi-parent inheritance
        // rule before we write any state. Project the agent's declared
        // execution block (or an empty one) onto the resolver's input
        // shape, then ask the resolver to intersect against each parent
        // unit's persisted defaults. If the resolution surfaces any
        // diverging field, the create is rejected with the structured 422
        // documented in the ADR — operators get to know about the conflict
        // at write time, when they have context to fix it. Wired before
        // the directory register so the resolver short-circuits without a
        // partial-create rollback dance (mirrors the unit-resolution loop
        // above).
        var agentOwnExecution = ProjectAgentOwnExecutionConfig(definition);
        var parentUnitIds = resolvedUnits
            .Select(u => u.Entry.ActorId)
            .ToList();
        var resolution = inheritanceResolver.ResolveAgentConfig(
            agentOwnExecution,
            parentUnitIds,
            tenantContext.CurrentTenantId,
            cancellationToken);
        if (resolution.ConflictingFields.Count > 0)
        {
            return MultiParentInheritanceProblems
                .MultiParentInheritanceConflict(resolution.ConflictingFields);
        }

        var actorGuid = Guid.NewGuid();
        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorGuid);
        var address = Address.ForIdentity("agent", actorGuid);
        var entry = new DirectoryEntry(
            address,
            actorGuid,
            request.DisplayName,
            request.Description,
            request.Role,
            DateTimeOffset.UtcNow);

        await directoryService.RegisterAsync(entry, cancellationToken);

        // Establish the requested unit memberships. Each write mirrors
        // what AssignUnitAgent does (membership row + unit-actor member
        // add + legacy cached pointer on the agent actor). The first unit
        // in the ordered list acts as the derived "primary" for wire-
        // compat surfaces (AgentMetadata.ParentUnit). Ordering is
        // preserved from the caller's UnitIds list — the repository's
        // CreatedAt tie-break picks whichever row was written first, and
        // we write them in declaration order.
        // #1492: resolve agent UUID from the newly-registered directory entry.
        // actorId was set above (line: actorId = Guid.NewGuid().ToString()).
        var agentUuid = Guid.Parse(actorId);

        for (var i = 0; i < resolvedUnits.Count; i++)
        {
            var (unitId, unitEntry) = resolvedUnits[i];

            // Membership is keyed by stable Guid id (#1492 / #1629).
            await membershipRepository.UpsertAsync(
                new UnitMembership(
                    UnitId: unitEntry.ActorId,
                    AgentId: agentUuid,
                    Enabled: true),
                cancellationToken);

            var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unitEntry.ActorId)), nameof(UnitActor));
            await unitProxy.AddMemberAsync(address, cancellationToken);
        }

        string? primaryUnit = null;
        if (resolvedUnits.Count > 0)
        {
            // Mirror the primary unit onto the legacy cached pointer on the
            // agent actor so any reader still consulting it sees a consistent
            // value. Authoritative source is the membership table. The
            // ParentUnit field carries the unit's display name (slug-shaped
            // navigation label) so existing readers continue to render it
            // unchanged; the canonical Guid identity lives on the membership
            // row. Top-level agents have no unit parent and leave this slot
            // unset.
            primaryUnit = resolvedUnits[0].Entry.DisplayName;
            var agentProxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
                new ActorId(actorId), nameof(AgentActor));
            await agentProxy.SetMetadataAsync(
                new AgentMetadata(ParentUnit: primaryUnit),
                cancellationToken);
        }

        // If the caller supplied a definition JSON document, persist it on
        // the AgentDefinitionEntity so IAgentDefinitionProvider can
        // surface the execution configuration to the dispatcher. This is
        // the YAML-only path for selecting the agent's runtime (tool /
        // image / provider / model) — required by #480 acceptance so
        // switching provider is a pure-configuration change, no code
        // edit.
        if (definition is { } def)
        {
            var entity = await db.AgentDefinitions
                .FirstOrDefaultAsync(a => a.Id == actorGuid, cancellationToken);
            if (entity is not null)
            {
                entity.Definition = def;
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        var primaryUnitGuid = resolvedUnits.Count > 0
            ? resolvedUnits[0].Entry.ActorId
            : (Guid?)null;
        var response = ToAgentResponse(
            entry,
            new AgentMetadata(ParentUnit: primaryUnit),
            parentUnitId: primaryUnitGuid);
        return Results.Created($"/api/v1/tenant/agents/{actorId}", response);
    }

    private static async Task<IResult> DeleteAgentAsync(
        string id,
        IDirectoryService directoryService,
        IUnitMembershipRepository membershipRepository,
        CancellationToken cancellationToken)
    {
        var address = Address.For("agent", id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);

        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        // Per #744 the last-membership guard lives on DeleteAsync — the
        // cascade path must bypass it, otherwise DELETE /agents/{id} would
        // fail for every agent whose membership count ≥ 1. DeleteAll* is
        // the authorised bulk-clear seam the repository exposes for this
        // purpose; call it before the directory unregister so the write
        // is persisted even if a downstream step hiccups.
        //
        // #1492: DeleteAllForAgentAsync takes the agent's stable Guid.
        await membershipRepository.DeleteAllForAgentAsync(entry.ActorId, cancellationToken);

        await directoryService.UnregisterAsync(address, cancellationToken);

        return Results.NoContent();
    }

    // -------------------------------------------------------------------------
    // Equipped skill bundles (#2360). The agent store feeds Layer 4 of the
    // assembled prompt — distinct from the unit store, which feeds Layer 2.
    // Endpoints below are the agent-subject side; UnitEndpoints maps the
    // unit-subject side against IUnitSkillBundleStore. The two stores share
    // the same JSON-state-store backing pattern keyed by subject id.
    // -------------------------------------------------------------------------

    private static async Task<IResult> GetEquippedSkillsAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IAgentSkillBundleStore bundleStore,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
        var bundles = await bundleStore.GetAsync(actorId, cancellationToken);
        return Results.Ok(new EquippedSkillsResponse(EquippedSkillsProjection.From(bundles)));
    }

    private static async Task<IResult> EquipAgentSkillAsync(
        string id,
        EquipSkillRequest request,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IAgentSkillBundleStore bundleStore,
        CancellationToken cancellationToken)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.PackageName)
            || string.IsNullOrWhiteSpace(request.SkillName))
        {
            return Results.Problem(
                detail: "Both 'packageName' and 'skillName' are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var entry = await directoryService.ResolveAsync(Address.For("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
        try
        {
            var updated = await bundleStore.AddAsync(
                actorId,
                new Cvoya.Spring.Core.Skills.SkillBundleReference(request.PackageName, request.SkillName),
                cancellationToken);
            return Results.Ok(new EquippedSkillsResponse(EquippedSkillsProjection.From(updated)));
        }
        catch (Cvoya.Spring.Core.Skills.SkillBundlePackageNotFoundException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (Cvoya.Spring.Core.Skills.SkillBundleNotFoundException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> UnequipAgentSkillAsync(
        string id,
        string packageName,
        string skillName,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IAgentSkillBundleStore bundleStore,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId);
        var updated = await bundleStore.RemoveAsync(actorId, packageName, skillName, cancellationToken);
        return Results.Ok(new EquippedSkillsResponse(EquippedSkillsProjection.From(updated)));
    }

    /// <summary>
    /// Projects a directory entry (+ optional actor-owned metadata) into the
    /// wire shape. When <paramref name="metadata"/> is <c>null</c> the
    /// response carries default values (<c>Enabled = true</c>, <c>ExecutionMode = Auto</c>)
    /// so callers can treat those fields as non-nullable.
    /// </summary>
    /// <param name="entry">The directory entry for the agent.</param>
    /// <param name="metadata">Optional agent metadata from the actor.</param>
    /// <param name="hostingMode">
    /// Optional hosting mode string (e.g. <c>"ephemeral"</c> / <c>"persistent"</c>)
    /// sourced from the agent's <c>execution.hosting</c> field (#572).
    /// </param>
    /// <param name="initiativeLevel">
    /// Optional effective initiative level sourced from the initiative engine (#573).
    /// </param>
    internal static AgentResponse ToAgentResponse(
        DirectoryEntry entry,
        AgentMetadata? metadata = null,
        string? hostingMode = null,
        InitiativeLevel? initiativeLevel = null,
        LifecycleStatus? lifecycleStatus = null,
        string? lifecycleError = null,
        Guid? parentUnitId = null,
        string? instructions = null,
        IReadOnlyList<EffectiveToolResponse>? effectiveTools = null,
        string? executionImage = null) =>
        new(
            entry.ActorId,
            // #2114: Name carries the canonical 32-char no-dash hex form
            // (Address.Path) so it matches UnitResponse.Name and is safe to
            // pass straight back into URL path params. Human-readable label
            // lives on DisplayName.
            entry.Address.Path,
            entry.DisplayName,
            entry.Description,
            entry.Role,
            entry.RegisteredAt,
            metadata?.Model,
            metadata?.Specialty,
            metadata?.Enabled ?? true,
            metadata?.ExecutionMode ?? AgentExecutionMode.Auto,
            metadata?.ParentUnit,
            // #2250: ParentUnitId is the Guid in 32-char no-dash form so the
            // portal / CLI can pass it straight into /units/{id}/... routes.
            // ParentUnit (above) stays as the human-readable display name.
            ParentUnitId: parentUnitId.HasValue && parentUnitId.Value != Guid.Empty
                ? Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(parentUnitId.Value)
                : null,
            HostingMode: hostingMode,
            InitiativeLevel: initiativeLevel.HasValue
                ? initiativeLevel.Value.ToString().ToLowerInvariant()
                : null,
            // #2156: surface the lifecycle outcome as a lowercase string so
            // the wire shape mirrors InitiativeLevel and avoids the
            // nullable-enum oneOf ambiguity that the contract-test JSON
            // Schema runner flags as a violation. The string surface
            // round-trips Active / Error verbatim — Kiota lowers it to a
            // plain string property on the generated client.
            LifecycleStatus: lifecycleStatus.HasValue
                ? lifecycleStatus.Value.ToString().ToLowerInvariant()
                : null,
            LifecycleError: lifecycleError,
            Instructions: instructions,
            // #2337 Sub D: effective-tools projection used by the portal's
            // Tools sub-tab. Only the single-subject GET path populates
            // this; list paths leave it as an empty list to keep the
            // OpenAPI shape branchless. Empty list when the resolver was
            // not invoked.
            EffectiveTools: effectiveTools ?? Array.Empty<EffectiveToolResponse>(),
            // #2348: effective execution.image tag (parent-merged via
            // IAgentDefinitionProvider) so the portal's Tools sub-tab Image
            // section can render the tag rather than the digest-suffixed
            // provenance string. Null when neither the agent nor its
            // parent unit declares an image, or when the list-path caller
            // did not resolve it.
            ExecutionImage: executionImage);

    /// <summary>
    /// Best-effort resolution of the subject's effective tool set via
    /// <see cref="IToolGrantResolver"/>. Returns an empty list (never
    /// <c>null</c>) on failure so the response shape stays branchless.
    /// Used by both <see cref="GetAgentAsync"/> and the unit-detail
    /// endpoint (via the shared wire DTO).
    /// </summary>
    internal static async Task<IReadOnlyList<EffectiveToolResponse>> TryResolveEffectiveToolsAsync(
        IToolGrantResolver resolver,
        Address subject,
        ILogger logger,
        string subjectId,
        CancellationToken cancellationToken)
    {
        try
        {
            var tools = await resolver.ResolveAsync(subject, cancellationToken);
            if (tools.Count == 0)
            {
                return Array.Empty<EffectiveToolResponse>();
            }

            var projected = new List<EffectiveToolResponse>(tools.Count);
            foreach (var tool in tools)
            {
                projected.Add(new EffectiveToolResponse(
                    tool.Name,
                    tool.Namespace,
                    tool.Description,
                    tool.Provenance,
                    tool.InheritedFromUnitName));
            }

            return projected;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to resolve effective tools for {Scheme} {SubjectId}; returning empty list.",
                subject.Scheme,
                subjectId);
            return Array.Empty<EffectiveToolResponse>();
        }
    }

    /// <summary>
    /// Best-effort read of the subject's effective <c>execution.image</c>
    /// slot via <see cref="IAgentDefinitionProvider"/>. The provider is
    /// the same path the dispatcher uses (#601 / #603), so for agents
    /// the merge with the primary parent unit's defaults flows through
    /// automatically — an agent that doesn't declare its own image
    /// inherits the unit's. Returns <c>null</c> (never throws) on a
    /// missing definition row or a transient provider failure. Used by
    /// both <see cref="GetAgentAsync"/> and the unit-detail endpoint
    /// (via the shared wire DTO) so #2348's tag-label rendering has a
    /// single resolution path.
    /// </summary>
    internal static async Task<string?> TryResolveExecutionImageAsync(
        IAgentDefinitionProvider provider,
        string subjectActorId,
        ILogger logger,
        string subjectId,
        CancellationToken cancellationToken)
    {
        try
        {
            var definition = await provider.GetByIdAsync(subjectActorId, cancellationToken);
            var image = definition?.Execution?.Image;
            return string.IsNullOrWhiteSpace(image) ? null : image;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to resolve execution.image for subject {SubjectId}; surfacing null on the response.",
                subjectId);
            return null;
        }
    }

    /// <summary>
    /// Best-effort read of the agent actor's metadata. A failure here is
    /// non-fatal — callers fall back to the wire defaults (see
    /// <see cref="ToAgentResponse(DirectoryEntry, AgentMetadata?)"/>) so a
    /// transient actor outage doesn't blank the agent from the directory.
    /// The failure is logged by the caller via <paramref name="logger"/>.
    /// </summary>
    internal static async Task<AgentMetadata?> TryGetAgentMetadataAsync(
        IActorProxyFactory actorProxyFactory,
        string actorId,
        CancellationToken cancellationToken,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
                new ActorId(actorId), nameof(AgentActor));
            return await proxy.GetMetadataAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "Failed to read metadata for agent actor {ActorId}; falling back to defaults.",
                actorId);
            return null;
        }
    }

    /// <summary>
    /// Reads the agent's actor-owned metadata and overrides
    /// <see cref="AgentMetadata.ParentUnit"/> with a server-side derivation
    /// from the membership table. The derivation rule is "first by
    /// <c>CreatedAt</c>" — C2b-1 leaves it simple; a future
    /// <c>IsPrimary</c> flag may refine this without a wire-shape change.
    /// See #160: the membership table is authoritative; the cached
    /// <c>Agent:ParentUnit</c> state on the actor is a legacy mirror kept
    /// for non-critical readers and the backfill path.
    /// </summary>
    internal static async Task<(AgentMetadata? Metadata, Guid? ParentUnitId)> GetDerivedAgentMetadataAsync(
        IAgentActor proxy,
        IUnitMembershipRepository membershipRepository,
        Guid agentActorUuid,
        IDirectoryService? directoryService = null,
        CancellationToken cancellationToken = default)
    {
        AgentMetadata? metadata = null;
        try
        {
            metadata = await proxy.GetMetadataAsync(cancellationToken);
        }
        catch
        {
            // Falls through to the membership-driven projection below.
        }

        // #1492: membership is now keyed by UUID. Derive the primary unit slug
        // by resolving the UUID back to the navigation-form address for the
        // ParentUnit field (slug-based legacy compat). #2250: also return the
        // primary unit Guid so the AgentResponse can carry it as ParentUnitId
        // — the form the portal needs to fetch unit-scoped resources without
        // a slug→guid round trip.
        string? derivedParent = null;
        Guid? primaryUnitId = null;
        if (agentActorUuid != Guid.Empty)
        {
            var memberships = await membershipRepository.ListByAgentAsync(agentActorUuid, cancellationToken);
            if (memberships.Count > 0)
            {
                primaryUnitId = memberships[0].UnitId;
                // Resolve Guid → slug via directory for the ParentUnit string field.
                if (directoryService is not null)
                {
                    var allEntries = await directoryService.ListAllAsync(cancellationToken);
                    var unitEntry = allEntries.FirstOrDefault(
                        e => string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase)
                             && e.ActorId == primaryUnitId.Value);
                    derivedParent = unitEntry?.DisplayName
                        ?? Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(primaryUnitId.Value);
                }
                else
                {
                    derivedParent = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(primaryUnitId.Value);
                }
            }
        }

        if (metadata is null)
        {
            // No actor state; synthesise a metadata record so the response
            // still carries the derived parent (and defaults for the rest).
            return (new AgentMetadata(ParentUnit: derivedParent), primaryUnitId);
        }

        return (metadata with { ParentUnit = derivedParent }, primaryUnitId);
    }

    /// <summary>
    /// Projects the agent's optional definition JSON onto the
    /// <see cref="AgentExecutionConfig"/> shape consumed by
    /// <see cref="IExecutionConfigInheritanceResolver"/>. Each field on the
    /// returned config is independently optional — a field absent from the
    /// document is left blank so the resolver treats it as a candidate for
    /// inheritance from the parent unit set (ADR-0039 §6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors the <c>execution:</c> object shape that
    /// <c>DbAgentDefinitionProvider.ExtractExecution</c> reads at dispatch
    /// time. The legacy <c>ai.environment</c> back-compat path is
    /// intentionally not honoured here — the create endpoint accepts the
    /// post-ADR-0038 wire shape only, and the resolver is consulted only
    /// for that path.
    /// </para>
    /// <para>
    /// <see cref="AgentExecutionConfig.AgentRuntimeId"/> is non-nullable on
    /// the record but is treated as nullable for inheritance — the resolver
    /// applies <c>NullIfBlank</c> to every input field, so an empty string
    /// surfaces as "inherit this slot from the parent set" the same way a
    /// null does for the optional slots.
    /// </para>
    /// </remarks>
    internal static AgentExecutionConfig ProjectAgentOwnExecutionConfig(JsonElement? definition)
    {
        // No definition supplied at all — every inheritable field is left
        // blank, agent-owned hosting falls back to the record default
        // (Ephemeral). The resolver returns this verbatim when the parent
        // set is empty (top-level agent) and otherwise treats every blank
        // slot as a candidate for inheritance.
        if (definition is not { } root || root.ValueKind != JsonValueKind.Object)
        {
            return new AgentExecutionConfig(string.Empty, Image: null);
        }

        if (!root.TryGetProperty("execution", out var exec) ||
            exec.ValueKind != JsonValueKind.Object)
        {
            return new AgentExecutionConfig(string.Empty, Image: null);
        }

        var agentRuntimeId = ReadStringOrNull(exec, "agent");
        var image = ReadStringOrNull(exec, "image");
        var provider = ReadStringOrNull(exec, "provider");
        var model = ReadStringOrNull(exec, "model");
        var hosting = ParseHostingMode(ReadStringOrNull(exec, "hosting"));

        return new AgentExecutionConfig(
            AgentRuntimeId: agentRuntimeId ?? string.Empty,
            Image: image,
            Hosting: hosting,
            Provider: provider,
            Model: model);
    }

    private static string? ReadStringOrNull(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    // ──────────────────────────────────────────────────────────────────────
    // #2364: lifecycle-state transition endpoints.
    // ──────────────────────────────────────────────────────────────────────

    private static async Task<IResult> StartAgentAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.AgentEndpoints");
        var entry = await directoryService.ResolveAsync(Address.For("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId)), nameof(AgentActor));

        var starting = await proxy.TransitionAsync(LifecycleStatus.Starting, cancellationToken);
        if (!starting.Success)
        {
            return Results.Conflict(new
            {
                Error = starting.RejectionReason,
                CurrentStatus = starting.CurrentStatus,
            });
        }

        var running = await proxy.TransitionAsync(LifecycleStatus.Running, cancellationToken);
        if (!running.Success)
        {
            logger.LogError(
                "Agent {AgentId} failed to transition to Running: {Reason}. Current status {Status}.",
                id, running.RejectionReason, running.CurrentStatus);
            return Results.Problem(
                title: "Agent start failed",
                detail: running.RejectionReason,
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Accepted(
            $"/api/v1/tenant/agents/{id}",
            new AgentLifecycleResponse(entry.ActorId, running.CurrentStatus));
    }

    private static async Task<IResult> StopAgentAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.AgentEndpoints");
        var entry = await directoryService.ResolveAsync(Address.For("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId)), nameof(AgentActor));

        var stopping = await proxy.TransitionAsync(LifecycleStatus.Stopping, cancellationToken);
        if (!stopping.Success)
        {
            return Results.Conflict(new
            {
                Error = stopping.RejectionReason,
                CurrentStatus = stopping.CurrentStatus,
            });
        }

        var stopped = await proxy.TransitionAsync(LifecycleStatus.Stopped, cancellationToken);
        if (!stopped.Success)
        {
            logger.LogError(
                "Agent {AgentId} failed to transition to Stopped: {Reason}. Current status {Status}.",
                id, stopped.RejectionReason, stopped.CurrentStatus);
            return Results.Problem(
                title: "Agent stop failed",
                detail: stopped.RejectionReason,
                statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Accepted(
            $"/api/v1/tenant/agents/{id}",
            new AgentLifecycleResponse(entry.ActorId, stopped.CurrentStatus));
    }

    private static async Task<IResult> RevalidateAgentAsync(
        string id,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IActorProxyFactory actorProxyFactory,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(Address.For("agent", id), cancellationToken);
        if (entry is null)
        {
            return Results.Problem(detail: $"Agent '{id}' not found", statusCode: StatusCodes.Status404NotFound);
        }

        var proxy = actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(entry.ActorId)), nameof(AgentActor));

        var current = await proxy.GetStatusAsync(cancellationToken);
        if (current != LifecycleStatus.Draft
            && current != LifecycleStatus.Error
            && current != LifecycleStatus.Stopped)
        {
            return Results.Problem(
                title: "Invalid state",
                detail: $"Agent '{id}' is {current}; revalidation is only allowed from Draft, Error, or Stopped.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "InvalidState",
                    ["currentStatus"] = current.ToString(),
                });
        }

        var transition = await proxy.TransitionAsync(LifecycleStatus.Validating, cancellationToken);
        if (!transition.Success)
        {
            return Results.Problem(
                title: "Invalid state",
                detail: transition.RejectionReason ?? "Agent could not enter Validating.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "InvalidState",
                    ["currentStatus"] = transition.CurrentStatus.ToString(),
                });
        }

        return Results.Accepted(
            $"/api/v1/tenant/agents/{id}",
            new AgentLifecycleResponse(entry.ActorId, transition.CurrentStatus));
    }
}

/// <summary>
/// Lifecycle-transition response for the agent <c>/start</c>, <c>/stop</c>,
/// and <c>/revalidate</c> endpoints (#2364). Mirrors
/// <c>UnitLifecycleResponse</c> shape.
/// </summary>
public sealed record AgentLifecycleResponse(Guid AgentId, LifecycleStatus Status);
