// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the membership endpoints introduced in C2b-1
/// (#160): <c>GET /api/v1/agents/{id}/memberships</c>,
/// <c>GET /api/v1/units/{id}/memberships</c>,
/// <c>PUT /api/v1/units/{unitId}/memberships/{agentAddress}</c>,
/// <c>DELETE /api/v1/units/{unitId}/memberships/{agentAddress}</c>.
/// </summary>
public class MembershipEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    // Stable UUIDs for the well-known test entities.
    // After #1492 all membership rows use UUID keys, and the endpoints
    // require Guid-parseable ActorIds in directory entries.
    private static readonly Guid AgentAdaUuid = new("aadaadaa-0000-0000-0000-000000000001");
    private static readonly Guid AgentHopperUuid = new("aadaadaa-0000-0000-0000-000000000002");
    private static readonly Guid UnitEngineeringUuid = new("ee1ee111-0000-0000-0000-000000000001");
    private static readonly Guid UnitMarketingUuid = new("ee1ee111-0000-0000-0000-000000000002");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    // Tracks directory entries registered via ArrangeDirectoryHit so that
    // ListAllAsync returns a consistent set for ToResponse slug resolution.
    private readonly List<DirectoryEntry> _arrangedEntries = [];

    public MembershipEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListAgentMemberships_UnknownAgent_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync("/api/v1/tenant/agents/ghost/memberships", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListAgentMemberships_ReturnsEveryMembership()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryHit("agent", "ada", AgentAdaUuid);

        await UpsertAsync(UnitEngineeringUuid, AgentAdaUuid);
        await UpsertAsync(UnitMarketingUuid, AgentAdaUuid, model: "gpt-4o");

        var response = await _client.GetAsync("/api/v1/tenant/agents/ada/memberships", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var list = await response.Content.ReadFromJsonAsync<List<UnitMembershipResponse>>(JsonOptions, ct);
        list.ShouldNotBeNull();
        list!.Count.ShouldBe(2);
        // UnitId is now the identity-form URI (#1492).
        list.ShouldContain(m => m.UnitId == $"unit:id:{UnitEngineeringUuid}");
        list.ShouldContain(m => m.UnitId == $"unit:id:{UnitMarketingUuid}" && m.Model == "gpt-4o");
    }

    [Fact]
    public async Task ListUnitMemberships_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync("/api/v1/tenant/units/ghost/memberships", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListUnitMemberships_ReturnsRowsWithOverrides()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryHit("unit", "engineering", UnitEngineeringUuid);
        ArrangeDirectoryHit("agent", "ada", AgentAdaUuid);
        ArrangeDirectoryHit("agent", "hopper", AgentHopperUuid);

        await UpsertAsync(UnitEngineeringUuid, AgentAdaUuid, specialty: "reviewer");
        await UpsertAsync(UnitEngineeringUuid, AgentHopperUuid, enabled: false);

        var response = await _client.GetAsync("/api/v1/tenant/units/engineering/memberships", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var list = await response.Content.ReadFromJsonAsync<List<UnitMembershipResponse>>(JsonOptions, ct);
        list.ShouldNotBeNull();
        list!.Count.ShouldBe(2);
        list.ShouldContain(m => m.AgentAddress == "ada" && m.Specialty == "reviewer" && m.Enabled);
        list.ShouldContain(m => m.AgentAddress == "hopper" && !m.Enabled);
    }

    // #1060 / #1490: every projected row carries a unified `member` column
    // whose value is the identity-form address "agent:id:<actorId>" when the
    // actor id is a valid UUID, so consumers get an unambiguous stable
    // identifier that cannot be confused with a slug-shaped actor id.
    [Fact]
    public async Task ListUnitMemberships_EachRow_CarriesIdentityFormMemberUri()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryHit("unit", "engineering", UnitEngineeringUuid);
        ArrangeDirectoryHit("agent", "ada", AgentAdaUuid);
        ArrangeDirectoryHit("agent", "hopper", AgentHopperUuid);

        await UpsertAsync(UnitEngineeringUuid, AgentAdaUuid);
        await UpsertAsync(UnitEngineeringUuid, AgentHopperUuid);

        var response = await _client.GetAsync("/api/v1/tenant/units/engineering/memberships", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var list = await response.Content.ReadFromJsonAsync<List<UnitMembershipResponse>>(JsonOptions, ct);
        list.ShouldNotBeNull();
        // Member must be the identity form, not the navigation form.
        list!.ShouldContain(m => m.AgentAddress == "ada" && m.Member == $"agent:id:{AgentAdaUuid}");
        list.ShouldContain(m => m.AgentAddress == "hopper" && m.Member == $"agent:id:{AgentHopperUuid}");
    }

    // #1060 / #1490: the same projection applies to the /agents/{id}/memberships
    // surface, since it goes through the same ToResponse helper.
    [Fact]
    public async Task ListAgentMemberships_EachRow_CarriesIdentityFormMemberUri()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryHit("agent", "ada", AgentAdaUuid);

        await UpsertAsync(UnitEngineeringUuid, AgentAdaUuid);
        await UpsertAsync(UnitMarketingUuid, AgentAdaUuid);

        var response = await _client.GetAsync("/api/v1/tenant/agents/ada/memberships", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var list = await response.Content.ReadFromJsonAsync<List<UnitMembershipResponse>>(JsonOptions, ct);
        list.ShouldNotBeNull();
        list!.ShouldAllBe(m => m.Member == $"agent:id:{AgentAdaUuid}");
    }

    // #1490: Regression guard — after #1492, endpoints that encounter a
    // non-UUID ActorId on a unit entry surface a 404 (no stable UUID
    // identity) rather than silently falling back. This pins the new contract.
    [Fact]
    public async Task ListUnitMemberships_SlugShapedUnitActorId_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryHit("unit", "engineering", "actor-eng"); // non-UUID actor id

        var response = await _client.GetAsync("/api/v1/tenant/units/engineering/memberships", ct);

        // #1492: the endpoint requires a UUID-parseable ActorId to key
        // membership lookups — non-UUID ids are rejected with 404.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpsertMembership_NewRow_Persists()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryHit("unit", "engineering", UnitEngineeringUuid);
        ArrangeDirectoryHit("agent", "ada", AgentAdaUuid);

        var body = new UpsertMembershipRequest(
            Model: "claude-opus",
            Specialty: "reviewer",
            Enabled: true,
            ExecutionMode: AgentExecutionMode.OnDemand);

        var response = await _client.PutAsJsonAsync(
            "/api/v1/tenant/units/engineering/memberships/ada", body, JsonOptions, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var persisted = await GetAsync(UnitEngineeringUuid, AgentAdaUuid);
        persisted.ShouldNotBeNull();
        persisted!.Model.ShouldBe("claude-opus");
        persisted.Specialty.ShouldBe("reviewer");
        persisted.Enabled.ShouldBeTrue();
        persisted.ExecutionMode.ShouldBe(AgentExecutionMode.OnDemand);
    }

    [Fact]
    public async Task UpsertMembership_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.PutAsJsonAsync(
            "/api/v1/tenant/units/ghost/memberships/ada", new UpsertMembershipRequest(), JsonOptions, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteMembership_ExistingRow_Returns204()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryHit("unit", "engineering", UnitEngineeringUuid);
        ArrangeDirectoryHit("unit", "marketing", UnitMarketingUuid);
        ArrangeDirectoryHit("agent", "ada", AgentAdaUuid);

        // Two memberships — the agent must retain at least one after the
        // delete, per the #744 invariant. Without the second row the
        // repository rejects the removal with 409.
        await UpsertAsync(UnitEngineeringUuid, AgentAdaUuid);
        await UpsertAsync(UnitMarketingUuid, AgentAdaUuid);

        var response = await _client.DeleteAsync(
            "/api/v1/tenant/units/engineering/memberships/ada", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await GetAsync(UnitEngineeringUuid, AgentAdaUuid)).ShouldBeNull();
        (await GetAsync(UnitMarketingUuid, AgentAdaUuid)).ShouldNotBeNull();
    }

    [Fact]
    public async Task DeleteMembership_UnknownRow_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();

        var response = await _client.DeleteAsync(
            "/api/v1/tenant/units/engineering/memberships/ghost", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteMembership_LastRow_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryHit("unit", "engineering", UnitEngineeringUuid);
        ArrangeDirectoryHit("agent", "ada", AgentAdaUuid);

        // Only one membership; removing it would orphan the agent, which
        // #744 forbids — the endpoint surfaces the repository's
        // AgentMembershipRequiredException as 409 Conflict.
        await UpsertAsync(UnitEngineeringUuid, AgentAdaUuid);

        var response = await _client.DeleteAsync(
            "/api/v1/tenant/units/engineering/memberships/ada", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // The membership must still exist — the rejection was not a soft fail.
        (await GetAsync(UnitEngineeringUuid, AgentAdaUuid)).ShouldNotBeNull();
    }

    [Fact]
    public async Task AgentResponse_ParentUnit_DerivedFromFirstMembership()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryHit("agent", "ada", AgentAdaUuid);
        ArrangeDirectoryHit("unit", "engineering", UnitEngineeringUuid);
        ArrangeDirectoryHit("unit", "marketing", UnitMarketingUuid);

        var agentProxy = Substitute.For<Cvoya.Spring.Dapr.Actors.IAgentActor>();
        agentProxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new AgentMetadata(Model: "claude-opus"));
        _factory.ActorProxyFactory
            .CreateActorProxy<Cvoya.Spring.Dapr.Actors.IAgentActor>(
                Arg.Is<global::Dapr.Actors.ActorId>(a => a.GetId() == AgentAdaUuid.ToString()),
                Arg.Any<string>())
            .Returns(agentProxy);

        // Add two memberships with deterministic CreatedAt ordering.
        await UpsertAsync(UnitEngineeringUuid, AgentAdaUuid);
        await Task.Delay(20, ct);
        await UpsertAsync(UnitMarketingUuid, AgentAdaUuid);

        var response = await _client.GetAsync("/api/v1/tenant/agents/ada", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var detail = await response.Content.ReadFromJsonAsync<AgentDetailResponse>(JsonOptions, ct);
        detail.ShouldNotBeNull();
        detail!.Agent.ParentUnit.ShouldBe("engineering");
    }

    // #1000: the actor-status payload rides the wire as a JSON string (not a
    // JSON object), matching CreateAgentRequest.DefinitionJson's convention.
    // The Kiota-generated client cannot round-trip the prior JsonElement?
    // shape because OpenAPI lowers JsonElement to an empty-schema oneOf,
    // producing an ambiguous composed type. Lock the wire contract so any
    // regression here fails the API test suite before reaching the CLI.
    [Fact]
    public async Task GetAgent_StatusField_IsStringOrNullOnTheWire()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryHit("agent", "ada", AgentAdaUuid);

        var agentProxy = Substitute.For<Cvoya.Spring.Dapr.Actors.IAgentActor>();
        agentProxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new AgentMetadata());
        _factory.ActorProxyFactory
            .CreateActorProxy<Cvoya.Spring.Dapr.Actors.IAgentActor>(
                Arg.Is<global::Dapr.Actors.ActorId>(a => a.GetId() == AgentAdaUuid.ToString()),
                Arg.Any<string>())
            .Returns(agentProxy);

        await UpsertAsync(UnitEngineeringUuid, AgentAdaUuid);

        var response = await _client.GetAsync("/api/v1/tenant/agents/ada", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var raw = await response.Content.ReadAsStringAsync(ct);
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        var statusKind = doc.RootElement.GetProperty("status").ValueKind;
        // Must be Null (no actor response routed through the stub) or String
        // (actor responded) — never a nested object or array. Kiota cannot
        // round-trip the object shape, which is what #1000 tripped on.
        statusKind.ShouldBeOneOf(
            System.Text.Json.JsonValueKind.Null,
            System.Text.Json.JsonValueKind.String);
    }

    [Fact]
    public async Task ListAgentMemberships_SurfacesIsPrimaryFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        ClearMemberships();
        ArrangeDirectoryHit("agent", "ada", AgentAdaUuid);

        await UpsertAsync(UnitEngineeringUuid, AgentAdaUuid);
        await Task.Delay(10, ct);
        await UpsertAsync(UnitMarketingUuid, AgentAdaUuid);

        var response = await _client.GetAsync("/api/v1/tenant/agents/ada/memberships", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var list = await response.Content.ReadFromJsonAsync<List<UnitMembershipResponse>>(JsonOptions, ct);
        list.ShouldNotBeNull();
        // UnitId is now the identity-form URI (#1492).
        list!.Single(m => m.UnitId == $"unit:id:{UnitEngineeringUuid}").IsPrimary.ShouldBeTrue();
        list.Single(m => m.UnitId == $"unit:id:{UnitMarketingUuid}").IsPrimary.ShouldBeFalse();
    }

    private void ClearMemberships()
    {
        _arrangedEntries.Clear();
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);
        _factory.DirectoryService
            .ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<DirectoryEntry>>(_arrangedEntries.AsReadOnly()));

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<Cvoya.Spring.Dapr.Data.SpringDbContext>();
        ctx.UnitMemberships.RemoveRange(ctx.UnitMemberships.ToList());
        ctx.SaveChanges();
    }

    /// <summary>
    /// Registers a directory entry with a UUID actor id so the endpoint can
    /// resolve slug → UUID. Also maintains <see cref="_arrangedEntries"/> so
    /// <c>ListAllAsync</c> returns a consistent set for slug-resolution in
    /// <c>ToResponse</c>.
    /// </summary>
    private void ArrangeDirectoryHit(string scheme, string path, Guid actorUuid)
        => ArrangeDirectoryHit(scheme, path, actorUuid.ToString());

    private void ArrangeDirectoryHit(string scheme, string path, string actorId)
    {
        var entry = new DirectoryEntry(
            new Address(scheme, path),
            actorId,
            path,
            $"{scheme} {path}",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == scheme && a.Path == path),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        // Keep the list in sync so ListAllAsync returns all arranged entries.
        _arrangedEntries.RemoveAll(e => e.Address.Scheme == scheme && e.Address.Path == path);
        _arrangedEntries.Add(entry);
    }

    private async Task UpsertAsync(
        Guid unitId,
        Guid agentId,
        string? model = null,
        string? specialty = null,
        bool enabled = true)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
        await repo.UpsertAsync(
            new UnitMembership(unitId, agentId, model, specialty, enabled),
            CancellationToken.None);
    }

    private async Task<UnitMembership?> GetAsync(Guid unitId, Guid agentId)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
        return await repo.GetAsync(unitId, agentId, CancellationToken.None);
    }
}