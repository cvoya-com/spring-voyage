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
    /// </summary>
    public async Task<AgentResponse> CreateAgentAsync(
        string id,
        string? displayName,
        string? role,
        CancellationToken ct = default)
    {
        var request = new CreateAgentRequest
        {
            Name = id,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName,
            Description = string.Empty,
            Role = role,
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
        CancellationToken ct = default)
    {
        var request = new CreateUnitRequest
        {
            Name = name,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName,
            Description = description ?? string.Empty,
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
            Color = string.IsNullOrWhiteSpace(color) ? null : color,
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
        };
        var result = await _client.Api.V1.Units.FromTemplate.PostAsync(request, cancellationToken: ct);
        return result ?? throw new InvalidOperationException(
            "Server returned an empty CreateUnitFromTemplate response.");
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

    // Directory

    /// <summary>Lists all directory entries.</summary>
    public async Task<IReadOnlyList<DirectoryEntryResponse>> ListDirectoryAsync(CancellationToken ct = default)
    {
        var result = await _client.Api.V1.Directory.GetAsync(cancellationToken: ct);
        return result ?? new List<DirectoryEntryResponse>();
    }

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
}