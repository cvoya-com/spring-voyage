// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli;

using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    /// <summary>
    /// Builds a client that issues requests through the supplied <paramref name="httpClient"/>.
    /// The HTTP client owns the auth header and any other shared configuration; this wrapper
    /// only owns the Kiota request adapter and base URL.
    /// </summary>
    public SpringApiClient(HttpClient httpClient, string baseUrl)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        var adapter = new HttpClientRequestAdapter(
            new AnonymousAuthenticationProvider(),
            httpClient: httpClient)
        {
            BaseUrl = baseUrl,
        };
        _client = new SpringApiKiotaClient(adapter);
    }

    // Agents

    /// <summary>
    /// Lists registered agents with optional server-side filtering.
    /// </summary>
    /// <param name="hosting">
    /// Optional hosting mode filter (ephemeral|persistent). Sent as <c>?hosting=</c>
    /// so the server filters before returning; an older server that ignores the
    /// param returns all agents and the CLI's client-side filter acts as a fallback.
    /// </param>
    /// <param name="initiative">
    /// Optional initiative level filter (repeated param for multi-value). Sent as
    /// <c>?initiative=</c> (repeated); an older server ignores these and the CLI
    /// falls back to client-side filtering.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<AgentResponse>> ListAgentsAsync(
        string? hosting = null,
        IReadOnlyList<string>? initiative = null,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Agents.GetAsync(
            config =>
            {
                // #1402: pass server-side filter params so callers don't have to
                // fetch the full list and filter in-memory. An older server that
                // doesn't support these params simply ignores them; the CLI's
                // client-side filter (in AgentCommand) acts as a defensive fallback.
                if (!string.IsNullOrWhiteSpace(hosting))
                {
                    config.QueryParameters.Hosting = hosting;
                }
                if (initiative is { Count: > 0 })
                {
                    config.QueryParameters.Initiative = initiative.ToArray();
                }
            },
            cancellationToken: ct);
        return result ?? new List<AgentResponse>();
    }

    /// <summary>
    /// Creates a new agent. The CLI's positional <paramref name="id"/> maps to the
    /// server's <c>Name</c> field (the unique identifier on the wire), while
    /// <paramref name="displayName"/> maps to <c>DisplayName</c>. Server requires both,
    /// so when no display name is supplied we fall back to <paramref name="id"/>.
    /// <paramref name="unitIds"/> carries the mandatory unit memberships (#744) —
    /// the server rejects the request with 400 when the list is empty.
    /// <paramref name="definitionJson"/> is the optional agent-definition JSON document
    /// (e.g. the execution block that selects <c>tool</c> / <c>image</c> / <c>provider</c>
    /// / <c>model</c>). When non-null the server persists it to
    /// <c>AgentDefinitions.Definition</c> so the dispatcher can honour it.
    /// </summary>
    public async Task<AgentResponse> CreateAgentAsync(
        string id,
        string? displayName,
        string? role,
        IReadOnlyList<string> unitIds,
        string? definitionJson = null,
        CancellationToken ct = default)
    {
        var request = new CreateAgentRequest
        {
            Name = id,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName,
            Description = string.Empty,
            Role = role,
            UnitIds = unitIds?.ToList() ?? new List<string>(),
            DefinitionJson = string.IsNullOrWhiteSpace(definitionJson) ? null : definitionJson,
        };

        var result = await _client.Api.V1.Tenant.Agents.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Server returned an empty CreateAgent response.");
    }

    /// <summary>Gets an agent's status detail.</summary>
    public async Task<AgentDetailResponse> GetAgentStatusAsync(string id, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Agents[id].GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException($"Server returned an empty status response for agent '{id}'.");
    }

    /// <summary>Deletes an agent.</summary>
    public Task DeleteAgentAsync(string id, CancellationToken ct = default)
        => _client.Api.V1.Tenant.Agents[id].DeleteAsync(cancellationToken: ct);

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
        var typed = new DeployPersistentAgentRequest
        {
            Image = string.IsNullOrWhiteSpace(image) ? null : image,
            Replicas = replicas,
        };
        var body = new Cvoya.Spring.Cli.Generated.Api.V1.Tenant.Agents.Item.Deploy.DeployRequestBuilder.DeployPostRequestBody
        {
            DeployPersistentAgentRequest = typed,
        };
        var result = await _client.Api.V1.Tenant.Agents[agentId].Deploy.PostAsync(body, cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Agents[agentId].Undeploy.PostAsync(cancellationToken: ct);
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
            Replicas = replicas,
        };
        var result = await _client.Api.V1.Tenant.Agents[agentId].Scale.PostAsync(request, cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Agents[agentId].Logs.GetAsync(
            config =>
            {
                if (tail is int t)
                {
                    config.QueryParameters.Tail = t;
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
        var result = await _client.Api.V1.Tenant.Agents[agentId].Deployment.GetAsync(cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Agents[agentId].Expertise.GetAsync(cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Agents[agentId].Expertise.PutAsync(body, cancellationToken: ct);
        return result?.Domains ?? new List<ExpertiseDomainDto>();
    }

    /// <summary>
    /// Gets a unit's own (non-aggregated) expertise domains.
    /// </summary>
    public async Task<IReadOnlyList<ExpertiseDomainDto>> GetUnitOwnExpertiseAsync(
        string unitId, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Units[unitId].Expertise.Own.GetAsync(cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Units[unitId].Expertise.Own.PutAsync(body, cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Units[unitId].Expertise.GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty aggregated-expertise response for unit '{unitId}'.");
    }

    // Units

    /// <summary>Lists all units.</summary>
    public async Task<IReadOnlyList<UnitResponse>> ListUnitsAsync(CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Units.GetAsync(cancellationToken: ct);
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
        IReadOnlyList<string>? parentUnitIds = null,
        bool? isTopLevel = null,
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
            // Review feedback on #744: forward the parent-required inputs
            // so the server enforces the invariant. The CLI catches the
            // neither/both case at parse time; the server remains the
            // source of truth.
            ParentUnitIds = parentUnitIds is { Count: > 0 } ? parentUnitIds.ToList() : null,
            IsTopLevel = isTopLevel,
        };
        var result = await _client.Api.V1.Tenant.Units.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Server returned an empty CreateUnit response.");
    }

    /// <summary>Starts a unit by posting to the /start endpoint.</summary>
    public async Task<UnitLifecycleResponse> StartUnitAsync(string id, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Units[id].Start.PostAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException($"Server returned an empty start response for unit '{id}'.");
    }

    /// <summary>Stops a unit by posting to the /stop endpoint.</summary>
    public async Task<UnitLifecycleResponse> StopUnitAsync(string id, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Units[id].Stop.PostAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException($"Server returned an empty stop response for unit '{id}'.");
    }

    /// <summary>
    /// Re-runs backend validation for a unit sitting in <c>Error</c> or
    /// <c>Stopped</c> (T-05 / #950). The server returns <c>202 Accepted</c>
    /// with the unit flipped back to <c>Validating</c> and a fresh
    /// workflow instance scheduled; it returns <c>409 Conflict</c> (wrapped
    /// in a Kiota <see cref="Microsoft.Kiota.Abstractions.ApiException"/>
    /// with <c>ResponseStatusCode == 409</c>) when the unit is in any other
    /// state. Callers surface the conflict through
    /// <c>UnitValidationExitCodes.UsageError</c> (exit 2) per the CLI
    /// contract on T-08.
    /// </summary>
    public async Task<UnitResponse> RevalidateUnitAsync(string id, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Units[id].Revalidate.PostAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty revalidate response for unit '{id}'.");
    }

    /// <summary>Gets the readiness status of a unit.</summary>
    public async Task<UnitReadinessResponse> GetUnitReadinessAsync(string id, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Units[id].Readiness.GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException($"Server returned an empty readiness response for unit '{id}'.");
    }

    /// <summary>Gets a unit's details.</summary>
    public async Task<UnitDetailResponse> GetUnitAsync(string id, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Units[id].GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException($"Server returned an empty response for unit '{id}'.");
    }

    /// <summary>
    /// Deletes a unit. When <paramref name="force"/> is <see langword="true"/>
    /// the API skips the lifecycle-status gate and removes the unit even
    /// from non-terminal states (Validating, Starting, Running, Stopping).
    /// Use to recover units the API otherwise refuses to delete with a
    /// 409 — see #1137 / <c>UnitEndpoints.DeleteUnitAsync</c>'s
    /// <c>forceHint</c>.
    /// </summary>
    public Task DeleteUnitAsync(string id, bool force = false, CancellationToken ct = default)
        => _client.Api.V1.Tenant.Units[id].DeleteAsync(
            requestConfiguration: force
                ? c => c.QueryParameters.Force = true
                : null,
            cancellationToken: ct);

    /// <summary>
    /// Lists all members of a unit (agents and sub-units) via the typed
    /// <c>GET /api/v1/units/{id}/members</c> endpoint.
    /// </summary>
    public async Task<IReadOnlyList<AddressDto>> ListUnitMembersAsync(
        string unitId,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Units[unitId].Members.GetAsync(cancellationToken: ct);
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
        return _client.Api.V1.Tenant.Units[unitId].Members.PostAsync(request, cancellationToken: ct);
    }

    /// <summary>Removes a member from a unit.</summary>
    public Task RemoveMemberAsync(string unitId, string memberId, CancellationToken ct = default)
        => _client.Api.V1.Tenant.Units[unitId].Members[memberId].DeleteAsync(cancellationToken: ct);

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
        var result = await _client.Api.V1.Tenant.Units[unitId].Memberships.GetAsync(cancellationToken: ct);
        return result ?? new List<UnitMembershipResponse>();
    }

    /// <summary>Lists every unit this agent belongs to, with per-membership config overrides.</summary>
    public async Task<IReadOnlyList<UnitMembershipResponse>> ListAgentMembershipsAsync(
        string agentId,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Agents[agentId].Memberships.GetAsync(cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Units[unitId].Memberships[agentId]
            .PutAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty UpsertMembership response for unit '{unitId}' / agent '{agentId}'.");
    }

    /// <summary>Removes the membership row for an agent in this unit.</summary>
    public Task DeleteMembershipAsync(string unitId, string agentId, CancellationToken ct = default)
        => _client.Api.V1.Tenant.Units[unitId].Memberships[agentId].DeleteAsync(cancellationToken: ct);

    // Unit policy (#453 — unified policy surface across all five UnitPolicy
    // dimensions: skill, model, cost, execution-mode, initiative). The
    // server exposes a single GET/PUT pair that carries every dimension as
    // an optional slot. `spring unit policy <dim> get|set|clear` composes
    // this pair with a merge helper in the CLI layer so per-dimension verbs
    // never need a per-dimension endpoint. Per-dimension endpoints would
    // have doubled the OpenAPI surface without unlocking anything the
    // unified shape does not already do.
    //
    // The Kiota-generated client is bypassed for these two calls (#999):
    // every dimension slot is a `oneOf [null, T]` which Kiota emits as an
    // IComposedTypeWrapper whose CreateFromDiscriminatorValue reads an
    // empty-string discriminator and leaves both branches null. That dropped
    // fields on read and crashed on the subsequent PUT's Serialize. Raw HTTP
    // + System.Text.Json against the plain UnitPolicyWire shape round-trips
    // cleanly without touching any other surface.

    /// <summary>
    /// Gets the unit's <see cref="UnitPolicyWire"/>. Returns the canonical
    /// empty shape (every dimension null) when the unit has never had a
    /// policy persisted — matches the server contract so callers never need
    /// to branch on 404 vs empty-policy.
    /// </summary>
    public async Task<UnitPolicyWire> GetUnitPolicyAsync(
        string unitId,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/tenant/units/{Uri.EscapeDataString(unitId)}/policy";
        using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
        await ThrowIfNotSuccessAsync(response, ct).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var wire = await System.Text.Json.JsonSerializer.DeserializeAsync<UnitPolicyWire>(
            stream, UnitPolicyJsonOptions, ct).ConfigureAwait(false);
        return wire ?? new UnitPolicyWire();
    }

    /// <summary>
    /// Upserts the unit's <see cref="UnitPolicyWire"/>. Sends the entire
    /// policy body verbatim — per-dimension semantics live in the CLI layer
    /// (it is responsible for reading the current policy, mutating only the
    /// target slot, and calling this method with the merged result). The
    /// server echoes the canonical post-write shape; returning it lets
    /// callers surface the merged view without a separate GET.
    /// </summary>
    public async Task<UnitPolicyWire> SetUnitPolicyAsync(
        string unitId,
        UnitPolicyWire policy,
        CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/v1/tenant/units/{Uri.EscapeDataString(unitId)}/policy";
        var json = System.Text.Json.JsonSerializer.Serialize(policy, UnitPolicyJsonOptions);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await _httpClient.PutAsync(url, content, ct).ConfigureAwait(false);
        await ThrowIfNotSuccessAsync(response, ct).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var wire = await System.Text.Json.JsonSerializer.DeserializeAsync<UnitPolicyWire>(
            stream, UnitPolicyJsonOptions, ct).ConfigureAwait(false);
        return wire ?? throw new InvalidOperationException(
            $"Server returned an empty policy response for unit '{unitId}'.");
    }

    /// <summary>
    /// JSON options for the raw unit-policy calls. Null-valued slots are
    /// omitted on the wire (server treats missing == cleared) to match the
    /// server's canonical post-write shape on the read side.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions UnitPolicyJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Wraps non-2xx HTTP responses as <see cref="HttpRequestException"/>
    /// with the response body included so scenarios surface the server
    /// error rather than a bare status code.
    /// </summary>
    private static async Task ThrowIfNotSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            body = string.Empty;
        }
        throw new HttpRequestException(
            $"Request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
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
        var result = await _client.Api.V1.Tenant.Units[unitId].Boundary.GetAsync(cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Units[unitId].Boundary.PutAsync(boundary, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty boundary response for unit '{unitId}'.");
    }

    /// <summary>
    /// Clears every boundary rule on the unit. Idempotent — calling on a
    /// unit that never had a boundary is a no-op and returns cleanly.
    /// </summary>
    public Task ClearUnitBoundaryAsync(string unitId, CancellationToken ct = default)
        => _client.Api.V1.Tenant.Units[unitId].Boundary.DeleteAsync(cancellationToken: ct);

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
        var result = await _client.Api.V1.Tenant.Units[unitId].Orchestration.GetAsync(cancellationToken: ct);
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
        var body = new UnitOrchestrationResponse
        {
            Strategy = strategyKey,
        };
        var result = await _client.Api.V1.Tenant.Units[unitId].Orchestration.PutAsync(body, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty orchestration response for unit '{unitId}'.");
    }

    /// <summary>
    /// Clears the unit's orchestration strategy. Idempotent — calling on a
    /// unit that never had a strategy persisted is a no-op and returns
    /// cleanly.
    /// </summary>
    public Task ClearUnitOrchestrationAsync(string unitId, CancellationToken ct = default)
        => _client.Api.V1.Tenant.Units[unitId].Orchestration.DeleteAsync(cancellationToken: ct);

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
        var result = await _client.Api.V1.Tenant.Units[unitId].Execution.GetAsync(cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Units[unitId].Execution.PutAsync(defaults, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty execution response for unit '{unitId}'.");
    }

    /// <summary>
    /// Clears the unit's execution defaults. Idempotent — calling on a
    /// unit that never had defaults declared is a no-op and returns
    /// cleanly.
    /// </summary>
    public Task ClearUnitExecutionAsync(string unitId, CancellationToken ct = default)
        => _client.Api.V1.Tenant.Units[unitId].Execution.DeleteAsync(cancellationToken: ct);

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
        var result = await _client.Api.V1.Tenant.Agents[agentId].Execution.GetAsync(cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Agents[agentId].Execution.PutAsync(shape, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty execution response for agent '{agentId}'.");
    }

    /// <summary>Clears the agent's execution block. Idempotent.</summary>
    public Task ClearAgentExecutionAsync(string agentId, CancellationToken ct = default)
        => _client.Api.V1.Tenant.Agents[agentId].Execution.DeleteAsync(cancellationToken: ct);

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
        var result = await _client.Api.V1.Tenant.Units[unitId].Humans.GetAsync(cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Units[unitId].Humans[humanId].Permissions
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
        => _client.Api.V1.Tenant.Units[unitId].Humans[humanId].Permissions.DeleteAsync(cancellationToken: ct);

    // Activity

    /// <summary>Queries activity events with optional filters and pagination.</summary>
    public async Task<ActivityQueryResult> QueryActivityAsync(
        string? source = null,
        string? eventType = null,
        string? severity = null,
        int? pageSize = null,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Activity.GetAsync(
            config =>
            {
                config.QueryParameters.Source = source;
                config.QueryParameters.EventType = eventType;
                config.QueryParameters.Severity = severity;
                config.QueryParameters.PageSize = pageSize;
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
        string? threadId,
        CancellationToken ct = default)
    {
        var request = new SendMessageRequest
        {
            To = new AddressDto { Scheme = toScheme, Path = toPath },
            Type = "Domain",
            ThreadId = threadId,
            Payload = new UntypedString(text),
        };
        var result = await _client.Api.V1.Tenant.Messages.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Server returned an empty SendMessage response.");
    }

    /// <summary>
    /// Fetches a single message (envelope + body) by id (#1209). Backs
    /// <c>spring message show &lt;id&gt;</c>.
    /// </summary>
    public async Task<MessageDetail> GetMessageAsync(Guid messageId, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Messages[messageId].GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty response for message '{messageId}'.");
    }

    // Threads (#452)

    /// <summary>
    /// Lists thread summaries, optionally filtered by unit, agent,
    /// status, or participant. Backs <c>spring thread list</c>.
    /// </summary>
    public async Task<IReadOnlyList<ThreadSummaryResponse>> ListThreadsAsync(
        string? unit = null,
        string? agent = null,
        string? status = null,
        string? participant = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Threads.GetAsync(
            config =>
            {
                config.QueryParameters.Unit = unit;
                config.QueryParameters.Agent = agent;
                config.QueryParameters.Status = status;
                config.QueryParameters.Participant = participant;
                config.QueryParameters.Limit = limit;
            },
            cancellationToken: ct);
        return result ?? new List<ThreadSummaryResponse>();
    }

    /// <summary>
    /// Fetches the detail view (summary + ordered events) for a single
    /// thread. Backs <c>spring thread show</c>.
    /// </summary>
    public async Task<ThreadDetailResponse> GetThreadAsync(string id, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Threads[id].GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException($"Server returned an empty response for thread '{id}'.");
    }

    /// <summary>
    /// Threads a new message into an existing thread. The CLI's
    /// <c>spring thread send --thread &lt;id&gt;</c> (and its
    /// <c>spring inbox respond</c> alias) both ride this single endpoint.
    /// </summary>
    /// <param name="threadId">The thread to send into.</param>
    /// <param name="toScheme">Destination address scheme (e.g. <c>agent</c>).</param>
    /// <param name="toPath">Destination address path (e.g. <c>ada</c>).</param>
    /// <param name="text">Free-text message body.</param>
    /// <param name="kind">
    /// Semantic kind of the message (#1421). Defaults to <c>information</c> when
    /// omitted. Use <c>answer</c> for <c>engagement answer</c> so the portal can
    /// distinguish replies from unprompted sends.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ThreadMessageResponse> SendThreadMessageAsync(
        string threadId,
        string toScheme,
        string toPath,
        string text,
        string? kind = null,
        CancellationToken ct = default)
    {
        var request = new ThreadMessageRequest
        {
            To = new AddressDto { Scheme = toScheme, Path = toPath },
            Text = text,
            Kind = string.IsNullOrWhiteSpace(kind) ? null : kind.ToLowerInvariant(),
        };
        var result = await _client.Api.V1.Tenant.Threads[threadId].Messages.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty message response for thread '{threadId}'.");
    }

    /// <summary>
    /// Closes (aborts) a thread across every participating agent
    /// (#1038). Backs <c>spring thread close &lt;id&gt;</c>. Returns
    /// the (now-closed) thread detail so the CLI can render a
    /// confirmation and the trailing event timeline including the
    /// <c>ThreadClosed</c> events the actors just emitted.
    /// </summary>
    public async Task<ThreadDetailResponse> CloseThreadAsync(
        string threadId,
        string? reason = null,
        CancellationToken ct = default)
    {
        var request = new CloseThreadRequest
        {
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
        };
        var result = await _client.Api.V1.Tenant.Threads[threadId].Close.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty close response for thread '{threadId}'.");
    }

    // Engagements (E2.2 / #1421) — SSE stream

    /// <summary>
    /// Streams live activity events for an engagement by connecting to the
    /// platform-wide SSE endpoint (<c>GET /api/v1/tenant/activity/stream</c>)
    /// with the <c>?thread=&lt;id&gt;</c> server-side filter that restricts the
    /// stream to events tagged with the specified thread id.
    ///
    /// <para>
    /// The server applies the filter via <c>ActivityEvent.CorrelationId</c> before
    /// events reach the wire, so only the events belonging to this engagement are
    /// sent over the connection. A client-side check is retained as a defensive
    /// fallback for older servers that predate the <c>?thread=</c> parameter — if
    /// an event slips through without a matching correlation id it is silently
    /// dropped on the client.
    /// </para>
    /// <para>
    /// Each raw SSE line is forwarded verbatim to <paramref name="onEvent"/>; the
    /// caller is responsible for stripping the <c>data: </c> prefix and parsing
    /// JSON if needed. The stream runs until the <paramref name="ct"/> is cancelled
    /// (Ctrl+C) or the server closes the connection.
    /// </para>
    /// </summary>
    /// <param name="threadId">Engagement (thread) id to filter on. Sent as <c>?thread=</c>.</param>
    /// <param name="source">
    /// Optional additional <c>?source=</c> query param to pass to the server
    /// (e.g. <c>agent://ada</c>). The server applies this filter before
    /// events reach the wire.
    /// </param>
    /// <param name="onEvent">
    /// Callback invoked with each non-empty SSE line. The line may include the
    /// <c>data: </c> prefix — callers must strip it before JSON parsing.
    /// </param>
    /// <param name="ct">Cancellation token; cancel to stop streaming.</param>
    public async Task StreamEngagementAsync(
        string threadId,
        string? source,
        Action<string> onEvent,
        CancellationToken ct = default)
    {
        // Build the URL with the server-side ?thread= filter. The server applies
        // the filter via ActivityEvent.CorrelationId (#1421), so only events
        // belonging to this engagement are transmitted over the wire.
        var queryParts = new List<string>
        {
            $"thread={Uri.EscapeDataString(threadId)}",
        };
        if (!string.IsNullOrWhiteSpace(source))
        {
            queryParts.Add($"source={Uri.EscapeDataString(source)}");
        }

        var url = $"{_baseUrl}/api/v1/tenant/activity/stream?{string.Join("&", queryParts)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("text/event-stream");

        // HttpCompletionOption.ResponseHeadersRead — do not buffer the body;
        // stream it line-by-line so events appear as soon as they arrive.
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Activity stream request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new System.IO.StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);

            // null means EOF — the server closed the connection.
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Each SSE event line looks like `data: <json>`.
            // Skip control lines that are not data lines.
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            // Defensive client-side fallback: for older servers that do not
            // support the ?thread= parameter the stream may include events from
            // other threads. Filter them out here so callers always see only the
            // engagement they requested.
            var json = line["data:".Length..].TrimStart();
            if (!json.Contains(threadId, StringComparison.Ordinal))
            {
                continue;
            }

            onEvent(line);
        }
    }

    // Inbox (#456)

    /// <summary>
    /// Lists inbox rows for the authenticated caller — threads awaiting
    /// a reply from the current <c>human://</c> address.
    /// </summary>
    public async Task<IReadOnlyList<InboxItemResponse>> ListInboxAsync(CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Inbox.GetAsync(cancellationToken: ct);
        return result ?? new List<InboxItemResponse>();
    }

    // Directory

    /// <summary>Lists all directory entries.</summary>
    public async Task<IReadOnlyList<DirectoryEntryResponse>> ListDirectoryAsync(CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Directory.GetAsync(cancellationToken: ct);
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
            Limit = limit,
            Offset = offset,
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

        var body = new Cvoya.Spring.Cli.Generated.Api.V1.Tenant.DirectoryNamespace.Search.SearchRequestBuilder.SearchPostRequestBody
        {
            DirectorySearchRequest = req,
        };
        var result = await _client.Api.V1.Tenant.Directory.Search.PostAsync(body, cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Server returned an empty search response.");
    }

    // Connectors
    //
    // The generic surface at /api/v1/connectors is connector-agnostic: it
    // lists every connector installed on the current tenant (#714) and
    // carries the pointer for a unit's current binding. Typed config lives
    // under /api/v1/connectors/{slug}/units/{unitId}/config and is owned by
    // each connector package — today only the GitHub connector has a typed
    // PUT generated into the Kiota client.

    /// <summary>
    /// Lists every connector installed on the current tenant (#714). The
    /// response carries both type-descriptor fields (slug, id, display name,
    /// URL templates) and install metadata (installedAt, updatedAt, config);
    /// pre-#714 this endpoint returned every connector the host knew about
    /// regardless of tenant-install state.
    /// </summary>
    public async Task<IReadOnlyList<InstalledConnectorResponse>> ListConnectorsAsync(
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Connectors.GetAsync(cancellationToken: ct);
        return result ?? new List<InstalledConnectorResponse>();
    }

    /// <summary>
    /// Returns the install envelope for a connector on the current tenant
    /// (#714) or <c>null</c> when the connector isn't installed. The server
    /// flipped this endpoint in #714 — a connector type registered with the
    /// host but not installed on the tenant now returns 404, mirroring the
    /// agent-runtime surface.
    /// </summary>
    public async Task<InstalledConnectorResponse?> GetConnectorAsync(
        string slugOrId, CancellationToken ct = default)
    {
        try
        {
            return await _client.Api.V1.Tenant.Connectors[slugOrId].GetAsync(cancellationToken: ct);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
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
        var result = await _client.Api.V1.Tenant.Connectors[slugOrId].Bindings.GetAsync(cancellationToken: ct);
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
            return await _client.Api.V1.Tenant.Units[unitId].Connector.GetAsync(cancellationToken: ct);
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
        string? reviewer = null,
        CancellationToken ct = default)
    {
        var request = new UnitGitHubConfigRequest
        {
            Owner = owner,
            Repo = repo,
            AppInstallationId = string.IsNullOrWhiteSpace(appInstallationId)
                ? null
                : long.TryParse(appInstallationId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedId)
                    ? parsedId
                    : null,
            Events = events?.ToList(),
            Reviewer = string.IsNullOrWhiteSpace(reviewer) ? null : reviewer,
        };
        var result = await _client.Api.V1.Tenant.Connectors.Github.Units[unitId].Config
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
            return await _client.Api.V1.Tenant.Connectors.Github.Units[unitId].Config
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
        var result = await _client.Api.V1.Tenant.Cost.Tenant.GetAsync(
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
        var result = await _client.Api.V1.Tenant.Cost.Units[unitId].GetAsync(
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
        var result = await _client.Api.V1.Tenant.Cost.Agents[agentId].GetAsync(
            config =>
            {
                config.QueryParameters.From = from;
                config.QueryParameters.To = to;
            },
            cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty agent cost response for '{agentId}'.");
    }

    // Cost breakdown by source — backs `spring analytics costs --by-source` (#554).
    // Hits the dashboard costs endpoint (/api/v1/tenant/dashboard/costs) which
    // already exists for the portal's Analytics surface; the CLI just reuses it.

    /// <summary>
    /// Gets the per-source cost breakdown from the dashboard costs endpoint.
    /// The response includes a <c>costsBySource</c> list (source address + total
    /// cost) and the overall <c>totalCost</c> for the window.
    /// </summary>
    public async Task<CostDashboardSummary> GetCostBreakdownAsync(
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Dashboard.Costs.GetAsync(
            config =>
            {
                config.QueryParameters.From = from;
                config.QueryParameters.To = to;
            },
            cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            "Server returned an empty cost breakdown response.");
    }

    // Cost time-series — backs `spring analytics costs --series` (#1361).
    // Hits /api/v1/tenant/analytics/agents/{id}/cost-timeseries or
    // /api/v1/tenant/analytics/units/{id}/cost-timeseries with optional
    // window + bucket query parameters.

    /// <summary>
    /// Gets the cost time-series for an agent over a window, bucketed by a
    /// fixed interval. Suitable for sparkline rendering in CLI or portal (#1361).
    /// </summary>
    public async Task<AnalyticsCostTimeseriesResponse> GetAgentCostTimeseriesAsync(
        string agentId,
        string? window = null,
        string? bucket = null,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Analytics.Agents[agentId].CostTimeseries.GetAsync(
            config =>
            {
                config.QueryParameters.Window = window;
                config.QueryParameters.Bucket = bucket;
            },
            cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty cost-timeseries response for agent '{agentId}'.");
    }

    /// <summary>
    /// Gets the cost time-series for a unit over a window, bucketed by a
    /// fixed interval. Suitable for sparkline rendering in CLI or portal (#1361).
    /// </summary>
    public async Task<AnalyticsCostTimeseriesResponse> GetUnitCostTimeseriesAsync(
        string unitId,
        string? window = null,
        string? bucket = null,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Analytics.Units[unitId].CostTimeseries.GetAsync(
            config =>
            {
                config.QueryParameters.Window = window;
                config.QueryParameters.Bucket = bucket;
            },
            cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty cost-timeseries response for unit '{unitId}'.");
    }

    // Per-agent model breakdown — backs `spring analytics costs --agent X --breakdown` (#1362).
    // Hits /api/v1/tenant/cost/agents/{id}/breakdown with optional from/to.

    /// <summary>
    /// Gets the per-model cost breakdown for an agent. Returns one entry per
    /// model used, descending by cost (#1362 / #570).
    /// </summary>
    public async Task<CostBreakdownResponse> GetAgentCostBreakdownAsync(
        string agentId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Cost.Agents[agentId].Breakdown.GetAsync(
            config =>
            {
                config.QueryParameters.From = from;
                config.QueryParameters.To = to;
            },
            cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty cost-breakdown response for agent '{agentId}'.");
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
        var result = await _client.Api.V1.Tenant.Analytics.Throughput.GetAsync(
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
        var result = await _client.Api.V1.Tenant.Analytics.Waits.GetAsync(
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
        var result = await _client.Api.V1.Tenant.Agents[agentId].Budget.PutAsync(request, cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Units[unitId].Budget.PutAsync(request, cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Agents[agentId].Budget.GetAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty GetAgentBudget response for agent '{agentId}'.");
    }

    /// <summary>Gets the daily cost budget for a unit.</summary>
    public async Task<BudgetResponse> GetUnitBudgetAsync(string unitId, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Units[unitId].Budget.GetAsync(cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Agents[agentId].Clones.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty CreateClone response for agent '{agentId}'.");
    }

    /// <summary>Lists the clones registered under an agent.</summary>
    public async Task<IReadOnlyList<CloneResponse>> ListClonesAsync(string agentId, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Agents[agentId].Clones.GetAsync(cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Agents[agentId].CloningPolicy.GetAsync(cancellationToken: ct);
        return result ?? new AgentCloningPolicyResponse();
    }

    /// <summary>Upserts the persistent cloning policy for an agent.</summary>
    public async Task<AgentCloningPolicyResponse> SetAgentCloningPolicyAsync(
        string agentId,
        AgentCloningPolicyResponse policy,
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Agents[agentId].CloningPolicy.PutAsync(policy, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty cloning-policy response for agent '{agentId}'.");
    }

    /// <summary>Clears the persistent cloning policy for an agent.</summary>
    public Task ClearAgentCloningPolicyAsync(string agentId, CancellationToken ct = default)
        => _client.Api.V1.Tenant.Agents[agentId].CloningPolicy.DeleteAsync(cancellationToken: ct);

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
        var result = await _client.Api.V1.Tenant.CloningPolicy.PutAsync(policy, cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Auth.Tokens.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException("Server returned an empty CreateToken response.");
    }

    /// <summary>Lists all API tokens.</summary>
    public async Task<IReadOnlyList<TokenResponse>> ListTokensAsync(CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.Auth.Tokens.GetAsync(cancellationToken: ct);
        return result ?? new List<TokenResponse>();
    }

    /// <summary>Revokes an API token by name.</summary>
    public Task RevokeTokenAsync(string name, CancellationToken ct = default)
        => _client.Api.V1.Tenant.Auth.Tokens[name].DeleteAsync(cancellationToken: ct);

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
        var result = await _client.Api.V1.Tenant.Units[unitId].Secrets.GetAsync(cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Units[unitId].Secrets.PostAsync(request, cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Units[unitId].Secrets[name].PutAsync(request, cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Units[unitId].Secrets[name].Versions.GetAsync(cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Units[unitId].Secrets[name].Prune.PostAsync(
            config =>
            {
                config.QueryParameters.Keep = keep;
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
                config.QueryParameters.Keep = keep;
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
                config.QueryParameters.Keep = keep;
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
        => _client.Api.V1.Tenant.Units[unitId].Secrets[name].DeleteAsync(cancellationToken: ct);

    /// <summary>Deletes a tenant-scoped secret (every version).</summary>
    public Task DeleteTenantSecretAsync(string name, CancellationToken ct = default)
        => _client.Api.V1.Tenant.Secrets[name].DeleteAsync(cancellationToken: ct);

    /// <summary>Deletes a platform-scoped secret (every version).</summary>
    public Task DeletePlatformSecretAsync(string name, CancellationToken ct = default)
        => _client.Api.V1.Platform.Secrets[name].DeleteAsync(cancellationToken: ct);

    // Platform-level connector provision / deprovision (#1259 / C1.2c).
    // Requires PlatformOperator role.

    /// <summary>
    /// Provisions a connector type platform-wide (idempotent). Requires
    /// PlatformOperator role.
    /// </summary>
    public async Task<ProvisionedConnectorResponse> ProvisionConnectorAsync(
        string slug, CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Platform.Connectors[slug].Provision.PostAsync(cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty provision response for connector '{slug}'.");
    }

    /// <summary>
    /// Deprovisions a connector type platform-wide. Requires PlatformOperator role.
    /// </summary>
    public Task DeprovisionConnectorAsync(string slug, CancellationToken ct = default)
        => _client.Api.V1.Platform.Connectors[slug].DeleteAsync(cancellationToken: ct);

    // Connector tenant-bind/unbind (#1259 / C1.2c). The `/install` verb was
    // renamed to `/bind` to clarify the authz split: platform provisions,
    // tenant binds.

    /// <summary>Binds (installs) a connector on the current tenant (idempotent).</summary>
    public async Task<InstalledConnectorResponse> BindConnectorAsync(
        string slugOrId, CancellationToken ct = default)
    {
        var body = new Cvoya.Spring.Cli.Generated.Api.V1.Tenant.Connectors.Item.Bind.BindRequestBuilder.BindPostRequestBody();
        var result = await _client.Api.V1.Tenant.Connectors[slugOrId].Bind.PostAsync(body, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty bind response for connector '{slugOrId}'.");
    }

    /// <summary>
    /// Unbinds (uninstalls) a connector from the current tenant. Targets
    /// <c>DELETE /api/v1/tenant/connectors/{slugOrId}</c>.
    /// </summary>
    public Task UnbindConnectorAsync(string slugOrId, CancellationToken ct = default)
        => _client.Api.V1.Tenant.Connectors[slugOrId].DeleteAsync(cancellationToken: ct);

    /// <summary>
    /// Returns the current credential-health row for a connector, or
    /// <c>null</c> when no validation has been recorded yet.
    /// </summary>
    public async Task<CredentialHealthResponse?> GetConnectorCredentialHealthAsync(
        string slugOrId,
        string? secretName = null,
        CancellationToken ct = default)
    {
        try
        {
            return await _client.Api.V1.Tenant.Connectors[slugOrId].CredentialHealth.GetAsync(
                config => { if (!string.IsNullOrWhiteSpace(secretName)) config.QueryParameters.SecretName = secretName; },
                cancellationToken: ct);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
    }

    // Agent runtimes (#688). Mirrors the /api/v1/agent-runtimes surface
    // landed in #715: install / list / show / models / config /
    // credential-health / refresh-models. The CLI `spring agent-runtime`
    // verbs ride these wrappers so the command layer stays free of Kiota
    // ceremony.

    /// <summary>Lists every agent runtime installed on the current tenant.</summary>
    public async Task<IReadOnlyList<InstalledAgentRuntimeResponse>> ListAgentRuntimesAsync(
        CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Tenant.AgentRuntimes.Installs.GetAsync(cancellationToken: ct);
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
            return await _client.Api.V1.Tenant.AgentRuntimes.Installs[id].GetAsync(cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.AgentRuntimes.Installs[id].Models.GetAsync(cancellationToken: ct);
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
        var body = new Cvoya.Spring.Cli.Generated.Api.V1.Tenant.AgentRuntimes.Installs.Item.Install.InstallRequestBuilder.InstallPostRequestBody
        {
            AgentRuntimeInstallRequest = new AgentRuntimeInstallRequest
            {
                Models = models?.ToList(),
                DefaultModel = defaultModel,
                BaseUrl = baseUrl,
            },
        };
        var result = await _client.Api.V1.Tenant.AgentRuntimes.Installs[id].Install.PostAsync(body, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty install response for agent runtime '{id}'.");
    }

    /// <summary>Uninstalls the runtime from the current tenant.</summary>
    public Task UninstallAgentRuntimeAsync(string id, CancellationToken ct = default)
        => _client.Api.V1.Tenant.AgentRuntimes.Installs[id].DeleteAsync(cancellationToken: ct);

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
        var result = await _client.Api.V1.Tenant.AgentRuntimes.Installs[id].Config.PatchAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty config response for agent runtime '{id}'.");
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
            return await _client.Api.V1.Tenant.AgentRuntimes.Installs[id].CredentialHealth.GetAsync(
                config => { if (!string.IsNullOrWhiteSpace(secretName)) config.QueryParameters.SecretName = secretName; },
                cancellationToken: ct);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the tenant-scoped configuration slot for an installed runtime
    /// (#1066). Returns <c>null</c> when the runtime is not registered with
    /// the host or is not installed on the current tenant — both surface as
    /// 404 from the server. Backs
    /// <c>spring agent-runtime config get &lt;id&gt;</c>.
    /// </summary>
    public async Task<AgentRuntimeConfigResponse?> GetAgentRuntimeConfigAsync(
        string id,
        CancellationToken ct = default)
    {
        try
        {
            return await _client.Api.V1.Tenant.AgentRuntimes.Installs[id].Config.GetAsync(cancellationToken: ct);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// Probes the runtime's backing service with the supplied
    /// <paramref name="credential"/> and records the outcome in the
    /// credential-health store (#1066). Does NOT touch the tenant's
    /// model list — the catalog rotation lives on the
    /// <c>refresh-models</c> path. Backs
    /// <c>spring agent-runtime validate-credential &lt;id&gt;</c>.
    /// </summary>
    public async Task<AgentRuntimeValidateCredentialResponse> ValidateAgentRuntimeCredentialAsync(
        string id,
        string? credential,
        string? secretName,
        CancellationToken ct = default)
    {
        var body = new Cvoya.Spring.Cli.Generated.Api.V1.Tenant.AgentRuntimes.Installs.Item.ValidateCredential.ValidateCredentialRequestBuilder.ValidateCredentialPostRequestBody
        {
            AgentRuntimeValidateCredentialRequest = new AgentRuntimeValidateCredentialRequest
            {
                Credential = credential,
                SecretName = string.IsNullOrWhiteSpace(secretName) ? null : secretName,
            },
        };
        var result = await _client.Api.V1.Tenant.AgentRuntimes.Installs[id].ValidateCredential.PostAsync(body, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty validate-credential response for agent runtime '{id}'.");
    }

    /// <summary>
    /// Asks the server to fetch the runtime's live model catalog from its
    /// backing service (e.g. the provider's <c>/v1/models</c> endpoint)
    /// and replace the tenant's stored list with the result. Backs
    /// <c>spring agent-runtime refresh-models &lt;id&gt;</c>.
    /// </summary>
    public async Task<InstalledAgentRuntimeResponse> RefreshAgentRuntimeModelsAsync(
        string id,
        string? credential,
        CancellationToken ct = default)
    {
        var body = new Cvoya.Spring.Cli.Generated.Api.V1.Tenant.AgentRuntimes.Installs.Item.RefreshModels.RefreshModelsRequestBuilder.RefreshModelsPostRequestBody
        {
            AgentRuntimeRefreshModelsRequest = new AgentRuntimeRefreshModelsRequest
            {
                Credential = credential,
            },
        };
        var result = await _client.Api.V1.Tenant.AgentRuntimes.Installs[id].RefreshModels.PostAsync(body, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            $"Server returned an empty refresh-models response for agent runtime '{id}'.");
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
        var result = await _client.Api.V1.Tenant.Packages.GetAsync(cancellationToken: ct);
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
            return await _client.Api.V1.Tenant.Packages[name].GetAsync(cancellationToken: ct);
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
        var result = await _client.Api.V1.Tenant.Packages.Templates.GetAsync(cancellationToken: ct);
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
            return await _client.Api.V1.Tenant.Packages[package].Templates[name].GetAsync(cancellationToken: ct);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            return null;
        }
    }

    // Package install / status / retry / abort / export (ADR-0035 decision 4).
    // These back the `spring package install|status|retry|abort|export` verb cluster.
    // We use _httpClient directly rather than Kiota-generated paths because the
    // install endpoints sit outside the /api/v1/tenant/ prefix and the file-upload
    // endpoint uses multipart/form-data which Kiota's generated adapters do not
    // handle cleanly. The JSON de/serialisation uses System.Text.Json with
    // camelCase policy so it matches the OpenAPI wire format.

    private static readonly System.Text.Json.JsonSerializerOptions PackageJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Installs one or more packages from the catalog as a single atomic batch.
    /// POST /api/v1/packages/install.
    /// </summary>
    public async Task<PackageInstallResponse> InstallPackagesAsync(
        IReadOnlyList<PackageInstallTargetRequest> targets,
        CancellationToken ct = default)
    {
        var body = new { targets };
        var json = System.Text.Json.JsonSerializer.Serialize(body, PackageJsonOptions);
        var content = new System.Net.Http.StringContent(
            json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync($"{_baseUrl}/api/v1/packages/install", content, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            ThrowForStatus(response.StatusCode, responseJson);
        }

        return System.Text.Json.JsonSerializer.Deserialize<PackageInstallResponse>(
            responseJson, PackageJsonOptions)
            ?? throw new InvalidOperationException("Server returned an empty install response.");
    }

    /// <summary>
    /// Installs a package from an uploaded local YAML file.
    /// POST /api/v1/packages/install/file (multipart/form-data).
    /// </summary>
    public async Task<PackageInstallResponse> InstallPackageFromFileAsync(
        string filePath,
        CancellationToken ct = default)
    {
        using var fileStream = System.IO.File.OpenRead(filePath);
        using var multipart = new System.Net.Http.MultipartFormDataContent();
        var fileContent = new System.Net.Http.StreamContent(fileStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-yaml");
        multipart.Add(fileContent, "file", System.IO.Path.GetFileName(filePath));

        var response = await _httpClient.PostAsync(
            $"{_baseUrl}/api/v1/packages/install/file", multipart, ct);
        var responseJson = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            ThrowForStatus(response.StatusCode, responseJson);
        }

        return System.Text.Json.JsonSerializer.Deserialize<PackageInstallResponse>(
            responseJson, PackageJsonOptions)
            ?? throw new InvalidOperationException("Server returned an empty install-from-file response.");
    }

    /// <summary>
    /// Gets install status including per-package detail.
    /// GET /api/v1/installs/{id}.
    /// Returns null when the install id is not found (404).
    /// </summary>
    public async Task<PackageInstallResponse?> GetInstallStatusAsync(
        string installId,
        CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(
            $"{_baseUrl}/api/v1/installs/{installId}", ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            ThrowForStatus(response.StatusCode, responseJson);
        }

        return System.Text.Json.JsonSerializer.Deserialize<PackageInstallResponse>(
            responseJson, PackageJsonOptions)
            ?? throw new InvalidOperationException("Server returned an empty install-status response.");
    }

    /// <summary>
    /// Re-runs Phase 2 for a failed install.
    /// POST /api/v1/installs/{id}/retry.
    /// Returns null when the install id is not found (404).
    /// </summary>
    public async Task<PackageInstallResponse?> RetryInstallAsync(
        string installId,
        CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsync(
            $"{_baseUrl}/api/v1/installs/{installId}/retry",
            new System.Net.Http.StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json"),
            ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            ThrowForStatus(response.StatusCode, responseJson);
        }

        return System.Text.Json.JsonSerializer.Deserialize<PackageInstallResponse>(
            responseJson, PackageJsonOptions)
            ?? throw new InvalidOperationException("Server returned an empty retry response.");
    }

    /// <summary>
    /// Discards staging rows for a failed install.
    /// POST /api/v1/installs/{id}/abort.
    /// Returns false when the install id is not found (404).
    /// </summary>
    public async Task<bool> AbortInstallAsync(string installId, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsync(
            $"{_baseUrl}/api/v1/installs/{installId}/abort",
            new System.Net.Http.StringContent(string.Empty, System.Text.Encoding.UTF8, "application/json"),
            ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            var responseJson = await response.Content.ReadAsStringAsync(ct);
            ThrowForStatus(response.StatusCode, responseJson);
        }

        return true;
    }

    /// <summary>
    /// Exports an installed package back to its original package.yaml.
    /// POST /api/v1/tenant/packages/export.
    /// Returns null when no package is found for the given unit name.
    /// </summary>
    public async Task<PackageExportResult?> ExportPackageAsync(
        string unitName,
        bool withValues = false,
        CancellationToken ct = default)
    {
        var body = new { unitName, withValues };
        var json = System.Text.Json.JsonSerializer.Serialize(body, PackageJsonOptions);
        var content = new System.Net.Http.StringContent(
            json, System.Text.Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(
            $"{_baseUrl}/api/v1/tenant/packages/export", content, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var responseJson = await response.Content.ReadAsStringAsync(ct);
            ThrowForStatus(response.StatusCode, responseJson);
        }

        var responseBytes = await response.Content.ReadAsByteArrayAsync(ct);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/x-yaml";
        var fileName = response.Content.Headers.ContentDisposition?.FileName ?? "package.yaml";
        return new PackageExportResult(responseBytes, contentType, fileName);
    }

    private static void ThrowForStatus(System.Net.HttpStatusCode statusCode, string responseJson)
    {
        // Extract the problem-details message if present; fall back to status code.
        string detail;
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(responseJson);
            detail = doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() ?? responseJson
                   : doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? responseJson
                   : responseJson;
        }
        catch
        {
            detail = responseJson;
        }

        throw new InvalidOperationException(
            $"Request failed with status {(int)statusCode}: {detail}");
    }

    /// <summary>
    /// A single package target in an install request.
    /// </summary>
    public sealed record PackageInstallTargetRequest(
        string PackageName,
        IReadOnlyDictionary<string, string>? Inputs);

    /// <summary>
    /// Response from the install/status/retry endpoints — maps the server's
    /// <c>InstallStatusResponse</c> shape.
    /// </summary>
    public sealed record PackageInstallResponse(
        Guid InstallId,
        string Status,
        IReadOnlyList<PackageInstallPackageDetail> Packages,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt,
        string? Error);

    /// <summary>Per-package detail within a <see cref="PackageInstallResponse"/>.</summary>
    public sealed record PackageInstallPackageDetail(
        string PackageName,
        string State,
        string? ErrorMessage);

    /// <summary>The bytes returned by the export endpoint, with content-type and suggested filename.</summary>
    public sealed record PackageExportResult(byte[] Content, string ContentType, string FileName);
}