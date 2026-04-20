// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Cli.Generated;
using Cvoya.Spring.Cli.Generated.Models;

using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Http.HttpClientLibrary;

/// <summary>
/// Strongly-typed wrapper around the Kiota-generated <see cref="SpringApiKiotaClient"/>.
/// Exposes one method per CLI use case so commands can stay free of Kiota
/// fluent-builder chains and request-builder ceremony.
/// </summary>
public class SpringApiClient
{
    private readonly SpringApiKiotaClient _client;

    /// <summary>
    /// Builds a client that issues requests through the supplied <paramref name="httpClient"/>.
    /// The HTTP client owns the auth header and any other shared configuration; this wrapper
    /// only owns the Kiota request adapter and base URL.
    /// </summary>
    public SpringApiClient(HttpClient httpClient, string baseUrl)
    {
        var adapter = new HttpClientRequestAdapter(
            new AnonymousAuthenticationProvider(),
            httpClient: httpClient)
        {
            BaseUrl = baseUrl,
        };
        _client = new SpringApiKiotaClient(adapter);
    }

    // Agents

    /// <summary>Lists all registered agents.</summary>
    public async Task<IReadOnlyList<AgentResponse>> ListAgentsAsync(CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Agents.GetAsync(cancellationToken: ct);
        return result ?? new List<AgentResponse>();
    }

    /// <summary>
    /// Creates a new agent. The CLI's positional <paramref name="id"/> maps to the
    /// server's <c>Name</c> field (the unique identifier on the wire), while
    /// <paramref name="displayName"/> maps to <c>DisplayName</c>. Server requires both,
    /// so when no display name is supplied we fall back to <paramref name="id"/>.
    /// <paramref name="definitionJson"/> is the optional agent-definition JSON document
    /// (e.g. the execution block that selects <c>tool</c> / <c>image</c> / <c>provider</c>
    /// / <c>model</c>). When non-null the server persists it to
    /// <c>AgentDefinitions.Definition</c> so the dispatcher can honour it.
    /// </summary>
    public async Task<AgentResponse> CreateAgentAsync(
        string id,
        string? displayName,
        string? role,
        string? definitionJson = null,
        CancellationToken ct = default)
    {
        var request = new CreateAgentRequest
        {
            Name = id,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName,
            Description = string.Empty,
            Role = role,
            DefinitionJson = string.IsNullOrWhiteSpace(definitionJson) ? null : definitionJson,
        };

        var result = await _client.Api.V1.Agents.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Server returned an empty CreateAgent response.");
    }

    /// <summary>Gets an agent's status detail.</summary>
    public async Task<AgentDetailResponse> GetAgentStatusAsync(string id, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Agents[id].GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException($"Server returned an empty status response for agent '{id}'.");
    }

    /// <summary>Deletes an agent.</summary>
    public Task DeleteAgentAsync(string id, CancellationToken ct = default)
        => _client.Api.V1.Agents[id].DeleteAsync(cancellationToken: ct);

    // Persistent-agent lifecycle (#396). Each verb maps 1:1 to the endpoint
    // of the same name under /api/v1/agents/{id}. The CLI layer composes
    // these into `spring agent deploy / undeploy / scale / logs`; the status
    // verb falls back to GetAgentStatusAsync above because the server
    // enriches that response with the deployment block when present.

    /// <summary>
    /// Deploys (or reconciles) the backing container for a persistent agent.
    /// Idempotent — redeploying a healthy agent is a no-op on the server.
    /// </summary>
    public async Task<PersistentAgentDeploymentResponse> DeployPersistentAgentAsync(
        string agentId,
        string? image = null,
        int? replicas = null,
        CancellationToken ct = default)
    {
        // OpenAPI 3.1 models nullable ints as the integer-or-null union, which
        // Kiota maps to UntypedNode. Wrap the C# int? in UntypedInteger when
        // present so the wire representation is a plain JSON number, not an
        // object. The policy/boundary wrappers above use the same oneOf
        // request-body pattern; mirror their discriminator shape.
        var typed = new DeployPersistentAgentRequest
        {
            Image = string.IsNullOrWhiteSpace(image) ? null : image,
            Replicas = replicas is int r ? new UntypedInteger(r) : null,
        };
        var body = new Cvoya.Spring.Cli.Generated.Api.V1.Agents.Item.Deploy.DeployRequestBuilder.DeployPostRequestBody
        {
            DeployPersistentAgentRequest = typed,
        };
        var result = await _client.Api.V1.Agents[agentId].Deploy.PostAsync(body, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty deploy response for agent '{agentId}'.");
    }

    /// <summary>
    /// Tears down the backing container for a persistent agent. Idempotent —
    /// undeploying an agent that is not running returns the canonical empty
    /// response.
    /// </summary>
    public async Task<PersistentAgentDeploymentResponse> UndeployPersistentAgentAsync(
        string agentId,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Agents[agentId].Undeploy.PostAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty undeploy response for agent '{agentId}'.");
    }

    /// <summary>
    /// Adjusts the replica count for a persistent agent. OSS core supports
    /// <c>0</c> (equivalent to undeploy) or <c>1</c>; anything else surfaces
    /// as a 400 with a clear "not supported yet" message.
    /// </summary>
    public async Task<PersistentAgentDeploymentResponse> ScalePersistentAgentAsync(
        string agentId,
        int replicas,
        CancellationToken ct = default)
    {
        var request = new ScalePersistentAgentRequest
        {
            Replicas = new UntypedInteger(replicas),
        };
        var result = await _client.Api.V1.Agents[agentId].Scale.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty scale response for agent '{agentId}'.");
    }

    /// <summary>
    /// Reads the tail of a persistent agent's container logs. Returns a 404
    /// (surfaced as an ApiException) when the agent is not currently deployed.
    /// </summary>
    public async Task<PersistentAgentLogsResponse> GetPersistentAgentLogsAsync(
        string agentId,
        int? tail = null,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Agents[agentId].Logs.GetAsync(
            config =>
            {
                if (tail is int t)
                {
                    // Kiota's query-param helper treats ints as strings when
                    // the OpenAPI spec doesn't pin format: int32. The server
                    // parses it back with int.TryParse, so sending the
                    // invariant-culture representation is fine.
                    config.QueryParameters.Tail = t.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            },
            cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty logs response for agent '{agentId}'.");
    }

    /// <summary>
    /// Fetches the current deployment state of a persistent agent without
    /// triggering a StatusQuery to the agent actor. Backs operators who want
    /// a cheap "is this agent up" probe.
    /// </summary>
    public async Task<PersistentAgentDeploymentResponse> GetPersistentAgentDeploymentAsync(
        string agentId,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Agents[agentId].Deployment.GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty deployment response for agent '{agentId}'.");
    }

    // Expertise (#412)

    /// <summary>
    /// Gets the configured expertise domains for an agent. Returns an empty
    /// list when the agent has no expertise set; the server distinguishes
    /// "not found" (404) from "empty" by throwing on the former.
    /// </summary>
    public async Task<IReadOnlyList<ExpertiseDomainDto>> GetAgentExpertiseAsync(
        string agentId, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Agents[agentId].Expertise.GetAsync(cancellationToken: ct);
        return result?.Domains ?? new List<ExpertiseDomainDto>();
    }

    /// <summary>
    /// Replaces an agent's expertise domains in full. Pass an empty list to
    /// clear the configuration.
    /// </summary>
    public async Task<IReadOnlyList<ExpertiseDomainDto>> SetAgentExpertiseAsync(
        string agentId,
        IReadOnlyList<ExpertiseDomainDto> domains,
        CancellationToken ct = default)
    {
        var body = new SetExpertiseRequest { Domains = domains?.ToList() ?? new List<ExpertiseDomainDto>() };
        var result = await _client.Api.V1.Agents[agentId].Expertise.PutAsync(body, cancellationToken: ct);
        return result?.Domains ?? new List<ExpertiseDomainDto>();
    }

    /// <summary>
    /// Gets a unit's own (non-aggregated) expertise domains.
    /// </summary>
    public async Task<IReadOnlyList<ExpertiseDomainDto>> GetUnitOwnExpertiseAsync(
        string unitId, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units[unitId].Expertise.Own.GetAsync(cancellationToken: ct);
        return result?.Domains ?? new List<ExpertiseDomainDto>();
    }

    /// <summary>
    /// Replaces a unit's own (non-aggregated) expertise domains in full.
    /// </summary>
    public async Task<IReadOnlyList<ExpertiseDomainDto>> SetUnitOwnExpertiseAsync(
        string unitId,
        IReadOnlyList<ExpertiseDomainDto> domains,
        CancellationToken ct = default)
    {
        var body = new SetExpertiseRequest { Domains = domains?.ToList() ?? new List<ExpertiseDomainDto>() };
        var result = await _client.Api.V1.Units[unitId].Expertise.Own.PutAsync(body, cancellationToken: ct);
        return result?.Domains ?? new List<ExpertiseDomainDto>();
    }

    /// <summary>
    /// Returns the unit's effective (recursive-aggregated) expertise. Each
    /// entry carries the contributing origin and the path walked to reach
    /// it, so peer-lookup callers can follow the origin one hop at a time.
    /// </summary>
    public async Task<AggregatedExpertiseResponse> GetUnitAggregatedExpertiseAsync(
        string unitId, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units[unitId].Expertise.GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty aggregated-expertise response for unit '{unitId}'.");
    }

    // Units

    /// <summary>Lists all units.</summary>
    public async Task<IReadOnlyList<UnitResponse>> ListUnitsAsync(CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units.GetAsync(cancellationToken: ct);
        return result ?? new List<UnitResponse>();
    }

    /// <summary>
    /// Creates a new unit. Server requires non-null Name/DisplayName/Description
    /// on <c>CreateUnitRequest</c>; optional inputs are normalised here so the
    /// server validator accepts them. <paramref name="model"/> and
    /// <paramref name="color"/> ride the same request body (#315) so the CLI
    /// and wizard share one create surface.
    /// </summary>
    public async Task<UnitResponse> CreateUnitAsync(
        string name,
        string? displayName,
        string? description,
        string? model = null,
        string? color = null,
        string? tool = null,
        string? provider = null,
        string? hosting = null,
        CancellationToken ct = default)
    {
        var request = new CreateUnitRequest
        {
            Name = name,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName,
            Description = description ?? string.Empty,
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
            Color = string.IsNullOrWhiteSpace(color) ? null : color,
            Tool = string.IsNullOrWhiteSpace(tool) ? null : tool,
            Provider = string.IsNullOrWhiteSpace(provider) ? null : provider,
            Hosting = string.IsNullOrWhiteSpace(hosting) ? null : hosting,
        };
        var result = await _client.Api.V1.Units.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Server returned an empty CreateUnit response.");
    }

    /// <summary>
    /// Creates a unit from a packaged template (#316). The server runs the
    /// skill-bundle resolver + validator + connector-binding preview and
    /// returns the created unit plus any non-fatal warnings. Optional
    /// <paramref name="unitName"/> maps to
    /// <c>CreateUnitFromTemplateRequest.UnitName</c> (#325), overriding the
    /// manifest-derived unit name so repeated instantiations of the same
    /// template do not collide.
    /// </summary>
    public async Task<UnitCreationResponse> CreateUnitFromTemplateAsync(
        string package,
        string templateName,
        string? unitName = null,
        string? displayName = null,
        string? model = null,
        string? color = null,
        string? tool = null,
        string? provider = null,
        string? hosting = null,
        CancellationToken ct = default)
    {
        var request = new CreateUnitFromTemplateRequest
        {
            Package = package,
            Name = templateName,
            UnitName = string.IsNullOrWhiteSpace(unitName) ? null : unitName,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
            Color = string.IsNullOrWhiteSpace(color) ? null : color,
            Tool = string.IsNullOrWhiteSpace(tool) ? null : tool,
            Provider = string.IsNullOrWhiteSpace(provider) ? null : provider,
            Hosting = string.IsNullOrWhiteSpace(hosting) ? null : hosting,
        };
        var result = await _client.Api.V1.Units.FromTemplate.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            "Server returned an empty CreateUnitFromTemplate response.");
    }

    /// <summary>Starts a unit by posting to the /start endpoint.</summary>
    public async Task<UnitLifecycleResponse> StartUnitAsync(string id, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units[id].Start.PostAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException($"Server returned an empty start response for unit '{id}'.");
    }

    /// <summary>Stops a unit by posting to the /stop endpoint.</summary>
    public async Task<UnitLifecycleResponse> StopUnitAsync(string id, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units[id].Stop.PostAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException($"Server returned an empty stop response for unit '{id}'.");
    }

    /// <summary>Gets the readiness status of a unit.</summary>
    public async Task<UnitReadinessResponse> GetUnitReadinessAsync(string id, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units[id].Readiness.GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException($"Server returned an empty readiness response for unit '{id}'.");
    }

    /// <summary>Gets a unit's details.</summary>
    public async Task<UnitDetailResponse> GetUnitAsync(string id, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units[id].GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException($"Server returned an empty response for unit '{id}'.");
    }

    /// <summary>Deletes a unit.</summary>
    public Task DeleteUnitAsync(string id, CancellationToken ct = default)
        => _client.Api.V1.Units[id].DeleteAsync(cancellationToken: ct);

    /// <summary>
    /// Lists all members of a unit (agents and sub-units) via the typed
    /// <c>GET /api/v1/units/{id}/members</c> endpoint.
    /// </summary>
    public async Task<IReadOnlyList<AddressDto>> ListUnitMembersAsync(
        string unitId,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units[unitId].Members.GetAsync(cancellationToken: ct);
        return result ?? new List<AddressDto>();
    }

    /// <summary>
    /// Adds a unit as a member of a parent unit (#331). The backend endpoint
    /// <c>POST /api/v1/units/{parentId}/members</c> accepts a scheme-tagged
    /// address, so this wrapper always sends <c>{ scheme: "unit", path: &lt;childId&gt; }</c>.
    /// Cycle-detection conflicts come back as 409 and bubble up as a
    /// <see cref="HttpRequestException"/> through Kiota's error handling.
    /// </summary>
    public Task AddUnitMemberAsync(string parentUnitId, string childUnitId, CancellationToken ct = default)
        => AddMemberAsync(parentUnitId, "unit", childUnitId, ct);

    /// <summary>Adds a member to a unit.</summary>
    public Task AddMemberAsync(
        string unitId,
        string memberScheme,
        string memberPath,
        CancellationToken ct = default)
    {
        var request = new AddMemberRequest
        {
            MemberAddress = new AddressDto
            {
                Scheme = memberScheme,
                Path = memberPath,
            },
        };
        return _client.Api.V1.Units[unitId].Members.PostAsync(request, cancellationToken: ct);
    }

    /// <summary>Removes a member from a unit.</summary>
    public Task RemoveMemberAsync(string unitId, string memberId, CancellationToken ct = default)
        => _client.Api.V1.Units[unitId].Members[memberId].DeleteAsync(cancellationToken: ct);

    // Unit memberships (per-membership config overrides — #245 / C2b-1).
    //
    // These are distinct from the actor-level "members" endpoint above: the
    // /memberships/* surface persists per-membership overrides (model,
    // specialty, enabled, executionMode) in the repository, while /members/*
    // adds/removes the agent from the unit actor's in-memory member list.
    // The two are complementary — the CLI's "unit members *" commands drive
    // the memberships endpoint because that is where config overrides live.

    /// <summary>Lists per-membership config for every agent that belongs to this unit.</summary>
    public async Task<IReadOnlyList<UnitMembershipResponse>> ListUnitMembershipsAsync(
        string unitId,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units[unitId].Memberships.GetAsync(cancellationToken: ct);
        return result ?? new List<UnitMembershipResponse>();
    }

    /// <summary>Lists every unit this agent belongs to, with per-membership config overrides.</summary>
    public async Task<IReadOnlyList<UnitMembershipResponse>> ListAgentMembershipsAsync(
        string agentId,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Agents[agentId].Memberships.GetAsync(cancellationToken: ct);
        return result ?? new List<UnitMembershipResponse>();
    }

    /// <summary>
    /// Creates or updates the per-membership config overrides for an agent in this unit.
    /// Only non-null overrides are sent on the wire; omitted fields leave the server's
    /// current value (or the agent-level default) in place.
    /// </summary>
    public async Task<UnitMembershipResponse> UpsertMembershipAsync(
        string unitId,
        string agentId,
        string? model,
        string? specialty,
        bool? enabled,
        AgentExecutionMode? executionMode,
        CancellationToken ct = default)
    {
        var request = new UpsertMembershipRequest
        {
            Model = model,
            Specialty = specialty,
            Enabled = enabled,
            ExecutionMode = executionMode is null
                ? null
                : new UpsertMembershipRequest.UpsertMembershipRequest_executionMode
                {
                    AgentExecutionMode = executionMode,
                },
        };
        var result = await _client.Api.V1.Units[unitId].Memberships[agentId]
            .PutAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty UpsertMembership response for unit '{unitId}' / agent '{agentId}'.");
    }

    /// <summary>Removes the membership row for an agent in this unit.</summary>
    public Task DeleteMembershipAsync(string unitId, string agentId, CancellationToken ct = default)
        => _client.Api.V1.Units[unitId].Memberships[agentId].DeleteAsync(cancellationToken: ct);

    // Unit policy (#453 — unified policy surface across all five UnitPolicy
    // dimensions: skill, model, cost, execution-mode, initiative). The
    // server exposes a single GET/PUT pair that carries every dimension as
    // an optional slot. `spring unit policy <dim> get|set|clear` composes
    // this pair with a merge helper in the CLI layer so per-dimension verbs
    // never need a per-dimension endpoint. Per-dimension endpoints would
    // have doubled the OpenAPI surface without unlocking anything the
    // unified shape does not already do.

    /// <summary>
    /// Gets the unit's <see cref="UnitPolicyResponse"/>. Returns the canonical
    /// empty shape (every dimension null) when the unit has never had a
    /// policy persisted — matches the server contract so callers never need
    /// to branch on 404 vs empty-policy.
    /// </summary>
    public async Task<UnitPolicyResponse> GetUnitPolicyAsync(
        string unitId,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units[unitId].Policy.GetAsync(cancellationToken: ct);
        return result ?? new UnitPolicyResponse();
    }

    /// <summary>
    /// Upserts the unit's <see cref="UnitPolicyResponse"/>. Sends the entire
    /// policy body verbatim — per-dimension semantics live in the CLI layer
    /// (it is responsible for reading the current policy, mutating only the
    /// target slot, and calling this method with the merged result). The
    /// server echoes the canonical post-write shape; returning it lets
    /// callers surface the merged view without a separate GET.
    /// </summary>
    public async Task<UnitPolicyResponse> SetUnitPolicyAsync(
        string unitId,
        UnitPolicyResponse policy,
        CancellationToken ct = default)
    {
        // The Kiota-generated PUT accepts a composed `oneOf` body. The OSS
        // contract shape we care about is always the fully-typed
        // UnitPolicyResponse branch — wrap it here so commands never have to
        // spell out the Member1 discriminator.
        var body = new Cvoya.Spring.Cli.Generated.Api.V1.Units.Item.Policy.PolicyRequestBuilder.PolicyPutRequestBody
        {
            UnitPolicyResponse = policy,
        };
        var result = await _client.Api.V1.Units[unitId].Policy.PutAsync(body, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty policy response for unit '{unitId}'.");
    }

    // Unit boundary (#413). Single unified endpoint returns the declared
    // opacity / projection / synthesis rules; PUT replaces; DELETE clears.

    /// <summary>
    /// Gets the unit's <see cref="UnitBoundaryResponse"/>. Returns the
    /// canonical empty shape when the unit has never had a boundary
    /// persisted — matches the server contract so callers never need to
    /// branch on 404 vs empty-boundary.
    /// </summary>
    public async Task<UnitBoundaryResponse> GetUnitBoundaryAsync(
        string unitId,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units[unitId].Boundary.GetAsync(cancellationToken: ct);
        return result ?? new UnitBoundaryResponse();
    }

    /// <summary>
    /// Upserts the unit's <see cref="UnitBoundaryResponse"/>. Sends the
    /// entire boundary body verbatim; the CLI merges the single target slot
    /// before calling this method for per-verb operations.
    /// </summary>
    public async Task<UnitBoundaryResponse> SetUnitBoundaryAsync(
        string unitId,
        UnitBoundaryResponse boundary,
        CancellationToken ct = default)
    {
        // Kiota emitted a composed oneOf body here (same shape it does for
        // /policy). Wrap the typed response into the Member1 discriminator
        // so the command layer never has to spell it out.
        var body = new Cvoya.Spring.Cli.Generated.Api.V1.Units.Item.Boundary.BoundaryRequestBuilder.BoundaryPutRequestBody
        {
            UnitBoundaryResponse = boundary,
        };
        var result = await _client.Api.V1.Units[unitId].Boundary.PutAsync(body, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty boundary response for unit '{unitId}'.");
    }

    /// <summary>
    /// Clears every boundary rule on the unit. Idempotent — calling on a
    /// unit that never had a boundary is a no-op and returns cleanly.
    /// </summary>
    public Task ClearUnitBoundaryAsync(string unitId, CancellationToken ct = default)
        => _client.Api.V1.Units[unitId].Boundary.DeleteAsync(cancellationToken: ct);

    // Unit orchestration (#606). Dedicated GET/PUT/DELETE surface for the
    // manifest-persisted `orchestration.strategy` key — the one ADR-0010
    // deliberately deferred. Rides the same UnitDefinitions.Definition JSON
    // the manifest-apply path writes (UnitCreationService), so either entry
    // point yields a wire-identical on-disk shape.

    /// <summary>
    /// Gets the unit's <see cref="UnitOrchestrationResponse"/>. Returns the
    /// canonical empty shape (<c>{ strategy: null }</c>) when the unit has
    /// no manifest-declared strategy — the resolver will pick via policy
    /// inference / unkeyed default per ADR-0010.
    /// </summary>
    public async Task<UnitOrchestrationResponse> GetUnitOrchestrationAsync(
        string unitId,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units[unitId].Orchestration.GetAsync(cancellationToken: ct);
        return result ?? new UnitOrchestrationResponse();
    }

    /// <summary>
    /// Upserts the unit's orchestration strategy key. An empty / whitespace
    /// key is rejected server-side with a 400 — use
    /// <see cref="ClearUnitOrchestrationAsync"/> to strip the slot.
    /// </summary>
    public async Task<UnitOrchestrationResponse> SetUnitOrchestrationAsync(
        string unitId,
        string strategyKey,
        CancellationToken ct = default)
    {
        // Kiota emitted a composed `oneOf` body here matching the /policy
        // and /boundary surfaces. Wrap the typed response into the
        // discriminator the generator produced so command callers never
        // have to spell it out.
        var body = new Cvoya.Spring.Cli.Generated.Api.V1.Units.Item.Orchestration.OrchestrationRequestBuilder.OrchestrationPutRequestBody
        {
            UnitOrchestrationResponse = new UnitOrchestrationResponse
            {
                Strategy = strategyKey,
            },
        };
        var result = await _client.Api.V1.Units[unitId].Orchestration.PutAsync(body, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty orchestration response for unit '{unitId}'.");
    }

    /// <summary>
    /// Clears the unit's orchestration strategy. Idempotent — calling on a
    /// unit that never had a strategy persisted is a no-op and returns
    /// cleanly.
    /// </summary>
    public Task ClearUnitOrchestrationAsync(string unitId, CancellationToken ct = default)
        => _client.Api.V1.Units[unitId].Orchestration.DeleteAsync(cancellationToken: ct);

    // Unit execution (#601 / #603 / #409 B-wide). Dedicated GET/PUT/DELETE
    // surface for the manifest-persisted unit `execution:` block (image /
    // runtime / tool / provider / model). Rides the same
    // UnitDefinitions.Definition JSON the manifest-apply path writes, so
    // either entry point yields a wire-identical on-disk shape.

    /// <summary>
    /// Gets the unit's <see cref="UnitExecutionResponse"/>. Returns the
    /// canonical empty shape (all fields <c>null</c>) when the unit has
    /// no manifest-declared execution defaults — agents will then need
    /// to declare their own image / tool / etc.
    /// </summary>
    public async Task<UnitExecutionResponse> GetUnitExecutionAsync(
        string unitId,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units[unitId].Execution.GetAsync(cancellationToken: ct);
        return result ?? new UnitExecutionResponse();
    }

    /// <summary>
    /// Upserts one or more fields on the unit's execution defaults.
    /// Partial update — null fields leave the corresponding slot alone.
    /// A body where every field is null is rejected server-side with a
    /// 400; use <see cref="ClearUnitExecutionAsync"/> to strip the block.
    /// </summary>
    public async Task<UnitExecutionResponse> SetUnitExecutionAsync(
        string unitId,
        UnitExecutionResponse defaults,
        CancellationToken ct = default)
    {
        var body = new Cvoya.Spring.Cli.Generated.Api.V1.Units.Item.Execution.ExecutionRequestBuilder.ExecutionPutRequestBody
        {
            UnitExecutionResponse = defaults,
        };
        var result = await _client.Api.V1.Units[unitId].Execution.PutAsync(body, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty execution response for unit '{unitId}'.");
    }

    /// <summary>
    /// Clears the unit's execution defaults. Idempotent — calling on a
    /// unit that never had defaults declared is a no-op and returns
    /// cleanly.
    /// </summary>
    public Task ClearUnitExecutionAsync(string unitId, CancellationToken ct = default)
        => _client.Api.V1.Units[unitId].Execution.DeleteAsync(cancellationToken: ct);

    // Agent execution (#601 / #603 / #409 B-wide). Symmetric with the
    // unit-execution surface, plus the agent-owned `hosting` field
    // (ephemeral / persistent) that never inherits.

    /// <summary>
    /// Gets the agent's declared <see cref="AgentExecutionResponse"/>.
    /// Returns all-null fields when the agent has no execution block on
    /// disk — at dispatch time the IAgentDefinitionProvider merges the
    /// parent unit's defaults on top.
    /// </summary>
    public async Task<AgentExecutionResponse> GetAgentExecutionAsync(
        string agentId,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Agents[agentId].Execution.GetAsync(cancellationToken: ct);
        return result ?? new AgentExecutionResponse();
    }

    /// <summary>
    /// Upserts one or more fields on the agent's execution block.
    /// Partial update — null fields leave the slot alone. All-null body
    /// is rejected with 400; use <see cref="ClearAgentExecutionAsync"/>.
    /// </summary>
    public async Task<AgentExecutionResponse> SetAgentExecutionAsync(
        string agentId,
        AgentExecutionResponse shape,
        CancellationToken ct = default)
    {
        var body = new Cvoya.Spring.Cli.Generated.Api.V1.Agents.Item.Execution.ExecutionRequestBuilder.ExecutionPutRequestBody
        {
            AgentExecutionResponse = shape,
        };
        var result = await _client.Api.V1.Agents[agentId].Execution.PutAsync(body, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty execution response for agent '{agentId}'.");
    }

    /// <summary>Clears the agent's execution block. Idempotent.</summary>
    public Task ClearAgentExecutionAsync(string agentId, CancellationToken ct = default)
        => _client.Api.V1.Agents[agentId].Execution.DeleteAsync(cancellationToken: ct);

    // Humans (#454). Three verbs — add, remove, list — all target the
    // server's /humans surface. `add` maps to PATCH
    // /humans/{humanId}/permissions; `remove` maps to DELETE on the same
    // path (added by this PR so the CLI doesn't have to round-trip through
    // a PATCH-to-viewer workaround); `list` maps to GET /humans.

    /// <summary>
    /// Lists every human permission entry for <paramref name="unitId"/>. The
    /// GET endpoint is Viewer-gated; an unauthorised caller surfaces as a
    /// Kiota <c>ApiException</c> carrying the 401/403.
    /// </summary>
    public async Task<IReadOnlyList<UnitPermissionEntry>> ListUnitHumanPermissionsAsync(
        string unitId,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units[unitId].Humans.GetAsync(cancellationToken: ct);
        return result ?? new List<UnitPermissionEntry>();
    }

    /// <summary>
    /// Sets / adds a human's permission entry on a unit. PATCH semantics —
    /// only the supplied fields are overwritten. Owner-gated on the server,
    /// so an Operator or Viewer caller surfaces an auth failure here.
    /// </summary>
    public async Task<SetHumanPermissionResponse> SetUnitHumanPermissionAsync(
        string unitId,
        string humanId,
        string permission,
        string? identity = null,
        bool? notifications = null,
        CancellationToken ct = default)
    {
        var request = new SetHumanPermissionRequest
        {
            Permission = permission,
            Identity = string.IsNullOrWhiteSpace(identity) ? null : identity,
            Notifications = notifications,
        };
        var result = await _client.Api.V1.Units[unitId].Humans[humanId].Permissions
            .PatchAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty SetHumanPermission response for unit '{unitId}' / human '{humanId}'.");
    }

    /// <summary>
    /// Removes a human's permission entry from a unit. Idempotent — DELETE
    /// on a human that has no entry still returns 204 so the CLI does not
    /// need to branch on prior presence.
    /// </summary>
    public Task RemoveUnitHumanPermissionAsync(
        string unitId,
        string humanId,
        CancellationToken ct = default)
        => _client.Api.V1.Units[unitId].Humans[humanId].Permissions.DeleteAsync(cancellationToken: ct);

    // Activity

    /// <summary>Queries activity events with optional filters and pagination.</summary>
    public async Task<ActivityQueryResult> QueryActivityAsync(
        string? source = null,
        string? eventType = null,
        string? severity = null,
        int? pageSize = null,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Activity.GetAsync(
            config =>
            {
                config.QueryParameters.Source = source;
                config.QueryParameters.EventType = eventType;
                config.QueryParameters.Severity = severity;
                config.QueryParameters.PageSize = pageSize?.ToString();
            },
            cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Server returned an empty activity query response.");
    }

    // Messages

    /// <summary>
    /// Sends a domain message wrapping the user-supplied text in an untyped JSON payload.
    /// The server requires <c>Type</c> (parseable as <c>MessageType</c>) and <c>Payload</c>
    /// (a JSON element); we send <c>Type=Domain</c> and <c>Payload</c>=the raw text string.
    /// </summary>
    public async Task<MessageResponse> SendMessageAsync(
        string toScheme,
        string toPath,
        string text,
        string? conversationId,
        CancellationToken ct = default)
    {
        var request = new SendMessageRequest
        {
            To = new AddressDto { Scheme = toScheme, Path = toPath },
            Type = "Domain",
            ConversationId = conversationId,
            Payload = new UntypedString(text),
        };
        var result = await _client.Api.V1.Messages.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Server returned an empty SendMessage response.");
    }

    // Conversations (#452)

    /// <summary>
    /// Lists conversation summaries, optionally filtered by unit, agent,
    /// status, or participant. Backs <c>spring conversation list</c>.
    /// </summary>
    public async Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(
        string? unit = null,
        string? agent = null,
        string? status = null,
        string? participant = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Conversations.GetAsync(
            config =>
            {
                config.QueryParameters.Unit = unit;
                config.QueryParameters.Agent = agent;
                config.QueryParameters.Status = status;
                config.QueryParameters.Participant = participant;
                // Kiota treats int32 query params as strings when the
                // OpenAPI `format: int32` hint is ignored (warning surfaced
                // at generation time); convert on the way out.
                config.QueryParameters.Limit = limit?.ToString();
            },
            cancellationToken: ct);
        return result ?? new List<ConversationSummary>();
    }

    /// <summary>
    /// Fetches the detail view (summary + ordered events) for a single
    /// conversation. Backs <c>spring conversation show</c>.
    /// </summary>
    public async Task<ConversationDetail> GetConversationAsync(string id, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Conversations[id].GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException($"Server returned an empty response for conversation '{id}'.");
    }

    /// <summary>
    /// Threads a new message into an existing conversation. The CLI's
    /// <c>spring conversation send --conversation &lt;id&gt;</c> (and its
    /// <c>spring inbox respond</c> alias) both ride this single endpoint.
    /// </summary>
    public async Task<ConversationMessageResponse> SendConversationMessageAsync(
        string conversationId,
        string toScheme,
        string toPath,
        string text,
        CancellationToken ct = default)
    {
        var request = new ConversationMessageRequest
        {
            To = new AddressDto { Scheme = toScheme, Path = toPath },
            Text = text,
        };
        var result = await _client.Api.V1.Conversations[conversationId].Messages.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty message response for conversation '{conversationId}'.");
    }

    // Inbox (#456)

    /// <summary>
    /// Lists inbox rows for the authenticated caller — conversations awaiting
    /// a reply from the current <c>human://</c> address.
    /// </summary>
    public async Task<IReadOnlyList<InboxItem>> ListInboxAsync(CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Inbox.GetAsync(cancellationToken: ct);
        return result ?? new List<InboxItem>();
    }

    // Directory

    /// <summary>Lists all directory entries.</summary>
    public async Task<IReadOnlyList<DirectoryEntryResponse>> ListDirectoryAsync(CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Directory.GetAsync(cancellationToken: ct);
        return result ?? new List<DirectoryEntryResponse>();
    }

    /// <summary>
    /// Searches the expertise directory (#542). Mirrors
    /// <c>POST /api/v1/directory/search</c>: free-text query + structured
    /// filters returning a ranked, paginated hit list. Both the portal and
    /// the CLI's <c>spring directory search</c> verb ride this wrapper.
    /// </summary>
    public async Task<DirectorySearchResponse> SearchDirectoryAsync(
        string? text,
        string? ownerScheme = null,
        string? ownerPath = null,
        IReadOnlyList<string>? domains = null,
        bool typedOnly = false,
        bool insideUnit = false,
        int? limit = null,
        int? offset = null,
        CancellationToken ct = default)
    {
        var req = new DirectorySearchRequest
        {
            Text = string.IsNullOrWhiteSpace(text) ? null : text,
            Domains = domains?.ToList(),
            TypedOnly = typedOnly,
            InsideUnit = insideUnit,
            // The int fields are modelled as UntypedNode because the
            // server's record defaults push them into an integer-or-null
            // union at the OpenAPI layer. Wrap concrete values in
            // UntypedInteger for a plain JSON number on the wire.
            Limit = limit is int l ? new UntypedInteger(l) : null,
            Offset = offset is int o ? new UntypedInteger(o) : null,
        };
        if (!string.IsNullOrWhiteSpace(ownerScheme) && !string.IsNullOrWhiteSpace(ownerPath))
        {
            req.Owner = new DirectorySearchRequest.DirectorySearchRequest_owner
            {
                AddressDto = new AddressDto
                {
                    Scheme = ownerScheme,
                    Path = ownerPath,
                },
            };
        }

        var body = new Cvoya.Spring.Cli.Generated.Api.V1.DirectoryNamespace.Search.SearchRequestBuilder.SearchPostRequestBody
        {
            DirectorySearchRequest = req,
        };
        var result = await _client.Api.V1.Directory.Search.PostAsync(body, cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Server returned an empty search response.");
    }

    // Connectors
    //
    // The generic surface at /api/v1/connectors is connector-agnostic: it
    // lists every registered connector type and carries the pointer for a
    // unit's current binding. Typed config lives under
    // /api/v1/connectors/{slug}/units/{unitId}/config and is owned by each
    // connector package — today only the GitHub connector has a typed PUT
    // generated into the Kiota client.

    /// <summary>
    /// Lists every connector type the server is aware of. This is the same
    /// data the web portal renders in its connector chooser, so
    /// <c>spring connector catalog</c> stays at parity with the UI.
    /// </summary>
    public async Task<IReadOnlyList<ConnectorTypeResponse>> ListConnectorsAsync(
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Connectors.GetAsync(cancellationToken: ct);
        return result ?? new List<ConnectorTypeResponse>();
    }

    /// <summary>
    /// Returns every unit bound to the given connector type (#520). Mirrors
    /// the portal's <c>useConnectorBindings</c> hook — both surfaces ride the
    /// same bulk endpoint so the CLI table and the portal's "Bound units"
    /// list are produced from the same data in one round-trip.
    /// </summary>
    public async Task<IReadOnlyList<ConnectorUnitBindingResponse>> ListConnectorBindingsAsync(
        string slugOrId,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Connectors[slugOrId].Bindings.GetAsync(cancellationToken: ct);
        return result ?? new List<ConnectorUnitBindingResponse>();
    }

    /// <summary>
    /// Returns the active connector binding pointer for a unit, or
    /// <c>null</c> when the unit is not bound to any connector. Mirrors the
    /// portal's handling of the 404 the server returns for an unbound unit.
    /// </summary>
    public async Task<UnitConnectorPointerResponse?> GetUnitConnectorAsync(
        string unitId,
        CancellationToken ct = default)
    {
        try
        {
            return await _client.Api.V1.Units[unitId].Connector.GetAsync(cancellationToken: ct);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            // Unbound unit — the CLI surfaces this as a distinct, non-error
            // state ("no binding") rather than a hard failure, matching the
            // portal's empty-chooser behaviour.
            return null;
        }
    }

    /// <summary>
    /// Binds a unit to GitHub and upserts its per-unit config atomically.
    /// Only connector type with a typed PUT surface today; other connectors
    /// are declined at the CLI layer with a clear error message until they
    /// ship a typed binding endpoint (tracked alongside their respective
    /// connector packages).
    /// </summary>
    public async Task<UnitGitHubConfigResponse> PutUnitGitHubConfigAsync(
        string unitId,
        string owner,
        string repo,
        string? appInstallationId,
        IReadOnlyList<string>? events,
        CancellationToken ct = default)
    {
        var request = new UnitGitHubConfigRequest
        {
            Owner = owner,
            Repo = repo,
            // The server accepts installationId as either a number or a
            // string depending on how the client serialises it. Kiota
            // models the field as UntypedNode; we pass it through as a
            // string node when supplied so downstream deserialisation
            // sees the raw value the operator typed.
            AppInstallationId = string.IsNullOrWhiteSpace(appInstallationId)
                ? null
                : new UntypedString(appInstallationId),
            Events = events?.ToList(),
        };
        var result = await _client.Api.V1.Connectors.Github.Units[unitId].Config
            .PutAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty PutUnitGitHubConfig response for unit '{unitId}'.");
    }

    /// <summary>
    /// Reads the GitHub config currently bound to a unit, or <c>null</c>
    /// when no GitHub binding exists (server returns 404). The CLI
    /// <c>connector show</c> verb uses this to enrich the generic binding
    /// pointer with the connector-specific config payload.
    /// </summary>
    public async Task<UnitGitHubConfigResponse?> GetUnitGitHubConfigAsync(
        string unitId,
        CancellationToken ct = default)
    {
        try
        {
            return await _client.Api.V1.Connectors.Github.Units[unitId].Config
                .GetAsync(cancellationToken: ct);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
    }

    // Costs — shared between `spring analytics costs` and the legacy
    // `spring cost summary` alias.

    /// <summary>Gets the tenant cost summary for a time range.</summary>
    public async Task<CostSummaryResponse> GetTenantCostAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Costs.Tenant.GetAsync(
            config =>
            {
                config.QueryParameters.From = from;
                config.QueryParameters.To = to;
            },
            cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Server returned an empty tenant cost response.");
    }

    /// <summary>Gets the cost summary for a unit.</summary>
    public async Task<CostSummaryResponse> GetUnitCostAsync(
        string unitId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Costs.Units[unitId].GetAsync(
            config =>
            {
                config.QueryParameters.From = from;
                config.QueryParameters.To = to;
            },
            cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty unit cost response for '{unitId}'.");
    }

    /// <summary>Gets the cost summary for an agent.</summary>
    public async Task<CostSummaryResponse> GetAgentCostAsync(
        string agentId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Costs.Agents[agentId].GetAsync(
            config =>
            {
                config.QueryParameters.From = from;
                config.QueryParameters.To = to;
            },
            cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty agent cost response for '{agentId}'.");
    }

    // Analytics — throughput + waits. The costs slice reuses the Costs
    // wrappers above because the portal's Costs tab and the CLI's `analytics
    // costs` verb both point at /api/v1/costs; adding a third aggregation
    // layer would fork the data source with no gain.

    /// <summary>
    /// Gets throughput counters (messages / turns / tool calls) per source over
    /// a time range. <paramref name="source"/> is a substring filter on the
    /// wire-format source address (e.g. <c>agent://</c>, <c>unit://eng-team</c>).
    /// </summary>
    public async Task<ThroughputRollupResponse> GetThroughputAsync(
        string? source = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Analytics.Throughput.GetAsync(
            config =>
            {
                config.QueryParameters.Source = source;
                config.QueryParameters.From = from;
                config.QueryParameters.To = to;
            },
            cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Server returned an empty analytics throughput response.");
    }

    /// <summary>
    /// Gets wait-time rollups per source: idle / busy / waiting-for-human
    /// durations derived from paired <c>StateChanged</c> lifecycle
    /// transitions, plus the raw <c>stateTransitions</c> event count (#476).
    /// </summary>
    public async Task<WaitTimeRollupResponse> GetWaitTimesAsync(
        string? source = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Analytics.Waits.GetAsync(
            config =>
            {
                config.QueryParameters.Source = source;
                config.QueryParameters.From = from;
                config.QueryParameters.To = to;
            },
            cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Server returned an empty analytics waits response.");
    }

    // Budgets — GET/PUT per scope. The server enforces DailyBudget > 0;
    // the CLI's `--period` flag normalises weekly/monthly amounts into a
    // daily figure before calling these so the wire contract stays stable.

    /// <summary>Sets the daily cost budget for an agent.</summary>
    public async Task<BudgetResponse> SetAgentBudgetAsync(
        string agentId,
        decimal dailyBudget,
        CancellationToken ct = default)
    {
        var request = new SetBudgetRequest { DailyBudget = (double)dailyBudget };
        var result = await _client.Api.V1.Agents[agentId].Budget.PutAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty SetAgentBudget response for agent '{agentId}'.");
    }

    /// <summary>Sets the daily cost budget for a unit.</summary>
    public async Task<BudgetResponse> SetUnitBudgetAsync(
        string unitId,
        decimal dailyBudget,
        CancellationToken ct = default)
    {
        var request = new SetBudgetRequest { DailyBudget = (double)dailyBudget };
        var result = await _client.Api.V1.Units[unitId].Budget.PutAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty SetUnitBudget response for unit '{unitId}'.");
    }

    /// <summary>Sets the daily cost budget for the tenant.</summary>
    public async Task<BudgetResponse> SetTenantBudgetAsync(
        decimal dailyBudget,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var request = new SetBudgetRequest { DailyBudget = (double)dailyBudget };
        var result = await _client.Api.V1.Tenant.Budget.PutAsync(
            request,
            config =>
            {
                config.QueryParameters.TenantId = tenantId;
            },
            cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Server returned an empty SetTenantBudget response.");
    }

    /// <summary>Gets the daily cost budget for an agent.</summary>
    public async Task<BudgetResponse> GetAgentBudgetAsync(string agentId, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Agents[agentId].Budget.GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty GetAgentBudget response for agent '{agentId}'.");
    }

    /// <summary>Gets the daily cost budget for a unit.</summary>
    public async Task<BudgetResponse> GetUnitBudgetAsync(string unitId, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units[unitId].Budget.GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty GetUnitBudget response for unit '{unitId}'.");
    }

    /// <summary>Gets the daily cost budget for the tenant.</summary>
    public async Task<BudgetResponse> GetTenantBudgetAsync(
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Budget.GetAsync(
            config =>
            {
                config.QueryParameters.TenantId = tenantId;
            },
            cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Server returned an empty GetTenantBudget response.");
    }

    // Clones — ride the same CloneType + AttachmentMode contract that the
    // portal's Create Clone action uses so both surfaces produce identical
    // clone identities and configuration.

    /// <summary>
    /// Creates a clone of an agent. <paramref name="cloneType"/> and
    /// <paramref name="attachmentMode"/> default to the portal's defaults
    /// (<see cref="CloningPolicy.EphemeralNoMemory"/> + <see cref="AttachmentMode.Detached"/>)
    /// so `spring agent clone create --agent ada` produces the same clone
    /// the UI would.
    /// </summary>
    public async Task<CloneResponse> CreateCloneAsync(
        string agentId,
        CloningPolicy cloneType = CloningPolicy.EphemeralNoMemory,
        AttachmentMode attachmentMode = AttachmentMode.Detached,
        CancellationToken ct = default)
    {
        var request = new CreateCloneRequest
        {
            CloneType = cloneType,
            AttachmentMode = attachmentMode,
        };
        var result = await _client.Api.V1.Agents[agentId].Clones.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty CreateClone response for agent '{agentId}'.");
    }

    /// <summary>Lists the clones registered under an agent.</summary>
    public async Task<IReadOnlyList<CloneResponse>> ListClonesAsync(string agentId, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Agents[agentId].Clones.GetAsync(cancellationToken: ct);
        return result ?? new List<CloneResponse>();
    }

    // Persistent cloning policy (#416). Back the `spring agent clone policy`
    // verbs and (internally) the tenant-wide surface. Kiota emits composed
    // oneOf bodies for the PUT operations so the wrappers pick the typed
    // member and hide the discriminator from the command layer — same
    // pattern used for /policy and /boundary.

    /// <summary>
    /// Gets the persistent cloning policy stored on an agent. Returns the
    /// canonical empty shape when no policy has been persisted so callers
    /// never need to branch on 404 vs empty-policy.
    /// </summary>
    public async Task<AgentCloningPolicyResponse> GetAgentCloningPolicyAsync(
        string agentId,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Agents[agentId].CloningPolicy.GetAsync(cancellationToken: ct);
        return result ?? new AgentCloningPolicyResponse();
    }

    /// <summary>Upserts the persistent cloning policy for an agent.</summary>
    public async Task<AgentCloningPolicyResponse> SetAgentCloningPolicyAsync(
        string agentId,
        AgentCloningPolicyResponse policy,
        CancellationToken ct = default)
    {
        var body = new Cvoya.Spring.Cli.Generated.Api.V1.Agents.Item.CloningPolicy.CloningPolicyRequestBuilder.CloningPolicyPutRequestBody
        {
            AgentCloningPolicyResponse = policy,
        };
        var result = await _client.Api.V1.Agents[agentId].CloningPolicy.PutAsync(body, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty cloning-policy response for agent '{agentId}'.");
    }

    /// <summary>Clears the persistent cloning policy for an agent.</summary>
    public Task ClearAgentCloningPolicyAsync(string agentId, CancellationToken ct = default)
        => _client.Api.V1.Agents[agentId].CloningPolicy.DeleteAsync(cancellationToken: ct);

    /// <summary>Gets the tenant-wide persistent cloning policy.</summary>
    public async Task<AgentCloningPolicyResponse> GetTenantCloningPolicyAsync(CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.CloningPolicy.GetAsync(cancellationToken: ct);
        return result ?? new AgentCloningPolicyResponse();
    }

    /// <summary>Upserts the tenant-wide persistent cloning policy.</summary>
    public async Task<AgentCloningPolicyResponse> SetTenantCloningPolicyAsync(
        AgentCloningPolicyResponse policy,
        CancellationToken ct = default)
    {
        var body = new Cvoya.Spring.Cli.Generated.Api.V1.Tenant.CloningPolicy.CloningPolicyRequestBuilder.CloningPolicyPutRequestBody
        {
            AgentCloningPolicyResponse = policy,
        };
        var result = await _client.Api.V1.Tenant.CloningPolicy.PutAsync(body, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            "Server returned an empty cloning-policy response for the tenant scope.");
    }

    /// <summary>Clears the tenant-wide persistent cloning policy.</summary>
    public Task ClearTenantCloningPolicyAsync(CancellationToken ct = default)
        => _client.Api.V1.Tenant.CloningPolicy.DeleteAsync(cancellationToken: ct);

    // Auth tokens

    /// <summary>Creates a new API token.</summary>
    public async Task<CreateTokenResponse> CreateTokenAsync(string name, CancellationToken ct = default)
    {
        var request = new CreateTokenRequest { Name = name };
        var result = await _client.Api.V1.Auth.Tokens.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Server returned an empty CreateToken response.");
    }

    /// <summary>Lists all API tokens.</summary>
    public async Task<IReadOnlyList<TokenResponse>> ListTokensAsync(CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Auth.Tokens.GetAsync(cancellationToken: ct);
        return result ?? new List<TokenResponse>();
    }

    /// <summary>Revokes an API token by name.</summary>
    public Task RevokeTokenAsync(string name, CancellationToken ct = default)
        => _client.Api.V1.Auth.Tokens[name].DeleteAsync(cancellationToken: ct);

    // Platform info (#451). The About panel on the portal and the
    // `spring platform info` CLI verb read the same endpoint so version
    // reporting can't drift between surfaces.

    /// <summary>
    /// Reads platform version, build hash, and license metadata. Mirrors
    /// the portal's Settings → About panel; the endpoint is anonymous so
    /// the client works before a caller has negotiated a token.
    /// </summary>
    public async Task<PlatformInfoResponse> GetPlatformInfoAsync(CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Platform.Info.GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Server returned an empty platform info response.");
    }

    // Secrets (#432). Unit / tenant / platform scope — each scope hangs off
    // its own top-level path on the server so the wrappers fan out to three
    // Kiota request-builder trees under the hood. The CLI `spring secret`
    // verbs pick the right scope and call through these wrappers so the
    // command layer stays free of Kiota discriminator ceremony. Plaintext
    // flows in only on the POST/PUT body; responses are metadata-only.

    /// <summary>
    /// Lists unit-scoped secret metadata. Never returns plaintext or store
    /// keys — the portal's Secrets tab and the CLI's <c>spring secret list
    /// --scope unit --unit &lt;id&gt;</c> share this single call so both
    /// surfaces render the same list.
    /// </summary>
    public async Task<IReadOnlyList<SecretMetadata>> ListUnitSecretsAsync(
        string unitId, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units[unitId].Secrets.GetAsync(cancellationToken: ct);
        return result?.Secrets ?? new List<SecretMetadata>();
    }

    /// <summary>Lists tenant-scoped secret metadata for the caller's tenant.</summary>
    public async Task<IReadOnlyList<SecretMetadata>> ListTenantSecretsAsync(
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Secrets.GetAsync(cancellationToken: ct);
        return result?.Secrets ?? new List<SecretMetadata>();
    }

    /// <summary>Lists platform-scoped secret metadata (admin-gated).</summary>
    public async Task<IReadOnlyList<SecretMetadata>> ListPlatformSecretsAsync(
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Platform.Secrets.GetAsync(cancellationToken: ct);
        return result?.Secrets ?? new List<SecretMetadata>();
    }

    /// <summary>
    /// Creates a unit-scoped secret. Exactly one of <paramref name="value"/>
    /// (pass-through write) or <paramref name="externalStoreKey"/> (bind
    /// existing reference) must be supplied; the server rejects the other
    /// combinations with 400.
    /// </summary>
    public async Task<CreateSecretResponse> CreateUnitSecretAsync(
        string unitId,
        string name,
        string? value,
        string? externalStoreKey,
        CancellationToken ct = default)
    {
        var request = new CreateSecretRequest
        {
            Name = name,
            Value = string.IsNullOrEmpty(value) ? null : value,
            ExternalStoreKey = string.IsNullOrWhiteSpace(externalStoreKey) ? null : externalStoreKey,
        };
        var result = await _client.Api.V1.Units[unitId].Secrets.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty CreateSecret response for unit '{unitId}' / '{name}'.");
    }

    /// <summary>Creates a tenant-scoped secret. Same value/external-key semantics as the unit variant.</summary>
    public async Task<CreateSecretResponse> CreateTenantSecretAsync(
        string name,
        string? value,
        string? externalStoreKey,
        CancellationToken ct = default)
    {
        var request = new CreateSecretRequest
        {
            Name = name,
            Value = string.IsNullOrEmpty(value) ? null : value,
            ExternalStoreKey = string.IsNullOrWhiteSpace(externalStoreKey) ? null : externalStoreKey,
        };
        var result = await _client.Api.V1.Tenant.Secrets.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty CreateSecret response for tenant-scoped '{name}'.");
    }

    /// <summary>Creates a platform-scoped secret (admin-gated).</summary>
    public async Task<CreateSecretResponse> CreatePlatformSecretAsync(
        string name,
        string? value,
        string? externalStoreKey,
        CancellationToken ct = default)
    {
        var request = new CreateSecretRequest
        {
            Name = name,
            Value = string.IsNullOrEmpty(value) ? null : value,
            ExternalStoreKey = string.IsNullOrWhiteSpace(externalStoreKey) ? null : externalStoreKey,
        };
        var result = await _client.Api.V1.Platform.Secrets.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty CreateSecret response for platform-scoped '{name}'.");
    }

    /// <summary>
    /// Rotates a unit-scoped secret by appending a new version. Exactly one
    /// of <paramref name="value"/> / <paramref name="externalStoreKey"/>
    /// must be supplied; origin can flip (pass-through ↔ external-ref) on
    /// rotation — see <c>docs/guide/secrets.md</c>.
    /// </summary>
    public async Task<RotateSecretResponse> RotateUnitSecretAsync(
        string unitId,
        string name,
        string? value,
        string? externalStoreKey,
        CancellationToken ct = default)
    {
        var request = new RotateSecretRequest
        {
            Value = string.IsNullOrEmpty(value) ? null : value,
            ExternalStoreKey = string.IsNullOrWhiteSpace(externalStoreKey) ? null : externalStoreKey,
        };
        var result = await _client.Api.V1.Units[unitId].Secrets[name].PutAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty RotateSecret response for unit '{unitId}' / '{name}'.");
    }

    /// <summary>Rotates a tenant-scoped secret by appending a new version.</summary>
    public async Task<RotateSecretResponse> RotateTenantSecretAsync(
        string name,
        string? value,
        string? externalStoreKey,
        CancellationToken ct = default)
    {
        var request = new RotateSecretRequest
        {
            Value = string.IsNullOrEmpty(value) ? null : value,
            ExternalStoreKey = string.IsNullOrWhiteSpace(externalStoreKey) ? null : externalStoreKey,
        };
        var result = await _client.Api.V1.Tenant.Secrets[name].PutAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty RotateSecret response for tenant-scoped '{name}'.");
    }

    /// <summary>Rotates a platform-scoped secret by appending a new version.</summary>
    public async Task<RotateSecretResponse> RotatePlatformSecretAsync(
        string name,
        string? value,
        string? externalStoreKey,
        CancellationToken ct = default)
    {
        var request = new RotateSecretRequest
        {
            Value = string.IsNullOrEmpty(value) ? null : value,
            ExternalStoreKey = string.IsNullOrWhiteSpace(externalStoreKey) ? null : externalStoreKey,
        };
        var result = await _client.Api.V1.Platform.Secrets[name].PutAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty RotateSecret response for platform-scoped '{name}'.");
    }

    /// <summary>
    /// Lists retained versions for a unit-scoped secret. The server marks
    /// the current version with <c>isCurrent=true</c>. Returns an empty
    /// shape when the secret does not exist (server sends 404, which Kiota
    /// surfaces as an <see cref="Microsoft.Kiota.Abstractions.ApiException"/>).
    /// </summary>
    public async Task<SecretVersionsListResponse> ListUnitSecretVersionsAsync(
        string unitId, string name, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units[unitId].Secrets[name].Versions.GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty versions response for unit '{unitId}' / '{name}'.");
    }

    /// <summary>Lists retained versions for a tenant-scoped secret.</summary>
    public async Task<SecretVersionsListResponse> ListTenantSecretVersionsAsync(
        string name, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Secrets[name].Versions.GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty versions response for tenant-scoped '{name}'.");
    }

    /// <summary>Lists retained versions for a platform-scoped secret.</summary>
    public async Task<SecretVersionsListResponse> ListPlatformSecretVersionsAsync(
        string name, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Platform.Secrets[name].Versions.GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty versions response for platform-scoped '{name}'.");
    }

    /// <summary>
    /// Prunes older versions of a unit-scoped secret, retaining the
    /// <paramref name="keep"/> most-recent. The current version is always
    /// retained regardless of <paramref name="keep"/>; the server returns
    /// 400 when <c>keep &lt; 1</c>.
    /// </summary>
    public async Task<PruneSecretResponse> PruneUnitSecretAsync(
        string unitId, string name, int keep, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Units[unitId].Secrets[name].Prune.PostAsync(
            config =>
            {
                // Kiota emits this as a string query parameter because the
                // OpenAPI `format: int32` hint is dropped during generation.
                config.QueryParameters.Keep = keep.ToString(System.Globalization.CultureInfo.InvariantCulture);
            },
            cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty prune response for unit '{unitId}' / '{name}'.");
    }

    /// <summary>Prunes older versions of a tenant-scoped secret.</summary>
    public async Task<PruneSecretResponse> PruneTenantSecretAsync(
        string name, int keep, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Secrets[name].Prune.PostAsync(
            config =>
            {
                config.QueryParameters.Keep = keep.ToString(System.Globalization.CultureInfo.InvariantCulture);
            },
            cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty prune response for tenant-scoped '{name}'.");
    }

    /// <summary>Prunes older versions of a platform-scoped secret.</summary>
    public async Task<PruneSecretResponse> PrunePlatformSecretAsync(
        string name, int keep, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Platform.Secrets[name].Prune.PostAsync(
            config =>
            {
                config.QueryParameters.Keep = keep.ToString(System.Globalization.CultureInfo.InvariantCulture);
            },
            cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty prune response for platform-scoped '{name}'.");
    }

    /// <summary>
    /// Deletes a unit-scoped secret (every version). Platform-owned store
    /// slots are reclaimed; external-reference versions leave the upstream
    /// store untouched.
    /// </summary>
    public Task DeleteUnitSecretAsync(string unitId, string name, CancellationToken ct = default)
        => _client.Api.V1.Units[unitId].Secrets[name].DeleteAsync(cancellationToken: ct);

    /// <summary>Deletes a tenant-scoped secret (every version).</summary>
    public Task DeleteTenantSecretAsync(string name, CancellationToken ct = default)
        => _client.Api.V1.Tenant.Secrets[name].DeleteAsync(cancellationToken: ct);

    /// <summary>Deletes a platform-scoped secret (every version).</summary>
    public Task DeletePlatformSecretAsync(string name, CancellationToken ct = default)
        => _client.Api.V1.Platform.Secrets[name].DeleteAsync(cancellationToken: ct);

    // Agent runtimes (#688). Mirrors the /api/v1/agent-runtimes surface
    // landed in #715: install / list / show / models / config / validate /
    // credential-health / verify-baseline. The CLI `spring agent-runtime`
    // verbs ride these wrappers so the command layer stays free of Kiota
    // ceremony.

    /// <summary>Lists every agent runtime installed on the current tenant.</summary>
    public async Task<IReadOnlyList<InstalledAgentRuntimeResponse>> ListAgentRuntimesAsync(
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.AgentRuntimes.GetAsync(cancellationToken: ct);
        return result ?? new List<InstalledAgentRuntimeResponse>();
    }

    /// <summary>
    /// Returns the install metadata for a runtime, or <c>null</c> when not installed.
    /// </summary>
    public async Task<InstalledAgentRuntimeResponse?> GetAgentRuntimeAsync(
        string id, CancellationToken ct = default)
    {
        try
        {
            return await _client.Api.V1.AgentRuntimes[id].GetAsync(cancellationToken: ct);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
    }

    /// <summary>Returns the tenant's configured model list for an installed runtime.</summary>
    public async Task<IReadOnlyList<AgentRuntimeModelResponse>> GetAgentRuntimeModelsAsync(
        string id, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.AgentRuntimes[id].Models.GetAsync(cancellationToken: ct);
        return result ?? new List<AgentRuntimeModelResponse>();
    }

    /// <summary>Installs (or refreshes) a runtime on the current tenant.</summary>
    public async Task<InstalledAgentRuntimeResponse> InstallAgentRuntimeAsync(
        string id,
        IReadOnlyList<string>? models,
        string? defaultModel,
        string? baseUrl,
        CancellationToken ct = default)
    {
        var body = new Cvoya.Spring.Cli.Generated.Api.V1.AgentRuntimes.Item.Install.InstallRequestBuilder.InstallPostRequestBody
        {
            AgentRuntimeInstallRequest = new AgentRuntimeInstallRequest
            {
                Models = models?.ToList(),
                DefaultModel = defaultModel,
                BaseUrl = baseUrl,
            },
        };
        var result = await _client.Api.V1.AgentRuntimes[id].Install.PostAsync(body, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty install response for agent runtime '{id}'.");
    }

    /// <summary>Uninstalls the runtime from the current tenant.</summary>
    public Task UninstallAgentRuntimeAsync(string id, CancellationToken ct = default)
        => _client.Api.V1.AgentRuntimes[id].DeleteAsync(cancellationToken: ct);

    /// <summary>Replaces the tenant-scoped config for an installed runtime.</summary>
    public async Task<InstalledAgentRuntimeResponse> UpdateAgentRuntimeConfigAsync(
        string id,
        IReadOnlyList<string> models,
        string? defaultModel,
        string? baseUrl,
        CancellationToken ct = default)
    {
        var request = new AgentRuntimeInstallConfig
        {
            Models = models.ToList(),
            DefaultModel = defaultModel,
            BaseUrl = baseUrl,
        };
        var result = await _client.Api.V1.AgentRuntimes[id].Config.PatchAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty config response for agent runtime '{id}'.");
    }

    /// <summary>
    /// Validates a candidate credential against the runtime's backing service
    /// and records the outcome in the credential-health store.
    /// </summary>
    public async Task<CredentialValidateResponse> ValidateAgentRuntimeCredentialAsync(
        string id,
        string credential,
        string? secretName,
        CancellationToken ct = default)
    {
        var request = new CredentialValidateRequest
        {
            Credential = credential,
            SecretName = secretName,
        };
        var result = await _client.Api.V1.AgentRuntimes[id].ValidateCredential.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty validate-credential response for agent runtime '{id}'.");
    }

    /// <summary>
    /// Returns the current credential-health row for a runtime, or <c>null</c>
    /// when no validation has been recorded yet.
    /// </summary>
    public async Task<CredentialHealthResponse?> GetAgentRuntimeCredentialHealthAsync(
        string id,
        string? secretName = null,
        CancellationToken ct = default)
    {
        try
        {
            return await _client.Api.V1.AgentRuntimes[id].CredentialHealth.GetAsync(
                config => { if (!string.IsNullOrWhiteSpace(secretName)) config.QueryParameters.SecretName = secretName; },
                cancellationToken: ct);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
    }

    /// <summary>Invokes the runtime's VerifyContainerBaselineAsync and returns the result.</summary>
    public async Task<ContainerBaselineCheckResponse> VerifyAgentRuntimeBaselineAsync(
        string id, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.AgentRuntimes[id].VerifyBaseline.PostAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty verify-baseline response for agent runtime '{id}'.");
    }

    // Packages (#395). Backs `spring package list / show` and
    // `spring template show <package>/<template>`. The portal's
    // /packages route consumes the same endpoints, so the CLI stays at
    // parity. The `list` and `show` shapes are forward compatible with
    // the Phase-6 install flow (#417 / PR-PLAT-PKG-2) — install adds a
    // new POST endpoint rather than changing the browse contract.

    /// <summary>
    /// Lists every installed package with per-package content counts.
    /// Matches the payload the portal's /packages card grid renders, so
    /// `spring package list` stays at parity with the UI.
    /// </summary>
    public async Task<IReadOnlyList<PackageSummary>> ListPackagesAsync(CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Packages.GetAsync(cancellationToken: ct);
        return result ?? new List<PackageSummary>();
    }

    /// <summary>
    /// Returns detailed contents for a single package (templates, agents,
    /// skills, connectors, workflows), or <c>null</c> when the package is
    /// not found. The 404 is normalised to null so callers surface a
    /// clean "not found" message rather than an exception.
    /// </summary>
    public async Task<PackageDetail?> GetPackageAsync(string name, CancellationToken ct = default)
    {
        try
        {
            return await _client.Api.V1.Packages[name].GetAsync(cancellationToken: ct);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// Lists every unit template discovered across packages. Kept as a
    /// convenience for the unit-creation wizard and the CLI's legacy
    /// `--output json` consumers; package-aware callers now prefer
    /// <see cref="ListPackagesAsync"/> + <see cref="GetPackageAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<UnitTemplateSummary>> ListUnitTemplatesAsync(CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Packages.Templates.GetAsync(cancellationToken: ct);
        return result ?? new List<UnitTemplateSummary>();
    }

    /// <summary>
    /// Returns the raw YAML + metadata for a single unit template, or
    /// <c>null</c> when the template is not found. Backs
    /// <c>spring template show &lt;package&gt;/&lt;template&gt;</c> and
    /// the portal's template preview card.
    /// </summary>
    public async Task<UnitTemplateDetail?> GetUnitTemplateAsync(
        string package,
        string name,
        CancellationToken ct = default)
    {
        try
        {
            return await _client.Api.V1.Packages[package].Templates[name].GetAsync(cancellationToken: ct);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
    }
}