// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the multi-parent execution-config inheritance
/// rule on the sub-unit assign path
/// (<c>POST /api/v1/tenant/units/{id}/members</c>, ADR-0039 §6 / B5).
/// When a unit-as-member would be left inheriting an inconsistent
/// execution-config field across the post-assignment parent set, the
/// endpoint must reject with 422 + structured
/// <c>MultiParentInheritanceConflict</c> body. Pinning the conflicting
/// field on the child must accept.
/// </summary>
public class UnitSubunitInheritanceEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly Guid ParentAUuid = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ParentBUuid = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid ParentCUuid = new("dddddddd-0000-0000-0000-000000000004");
    private static readonly Guid ChildUuid = new("cccccccc-0000-0000-0000-000000000003");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitSubunitInheritanceEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task AddMember_SubunitTwoParentsDivergingAgentRuntime_ChildInherits_Returns422()
    {
        // Setup: child unit C is already a sub-unit of parent A (agent runtime
        // claude-code). A second assignment to parent B (agent runtime
        // spring-voyage) would leave C inheriting a runtime that diverges
        // across its post-assignment parent set, so the endpoint must
        // reject with 422 and the MultiParentInheritanceConflict body
        // before touching actor state.
        var ct = TestContext.Current.CancellationToken;
        ResetState();

        ArrangeUnit(ParentAUuid, "parent-a");
        var parentBProxy = ArrangeUnit(ParentBUuid, "parent-b");
        ArrangeUnit(ChildUuid, "child");

        // Parent A and parent B carry diverging agent-runtime defaults.
        StubUnitDefaults(ParentAUuid, new UnitExecutionDefaults(Agent: "claude-code"));
        StubUnitDefaults(ParentBUuid, new UnitExecutionDefaults(Agent: "spring-voyage"));
        // Child has no own execution block — fully-inheriting.
        StubUnitDefaults(ChildUuid, defaults: null);

        // Seed the existing parent-A → child edge so the resolver sees
        // both parents in the post-assignment set.
        await UpsertSubunitEdgeAsync(ParentAUuid, ChildUuid, ct);

        // Act: POST to add the child as a sub-unit of parent B.
        var body = new AddMemberRequest(new AddressDto("unit", ChildUuid.ToString("N")));
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/tenant/units/{ParentBUuid:N}/members")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        var response = await _client.SendAsync(request, ct);

        // Assert: 422 with the structured body, and no actor-state write.
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        problem.GetProperty("error").GetString().ShouldBe("MultiParentInheritanceConflict");
        var conflictingFields = problem.GetProperty("conflictingFields");
        conflictingFields.TryGetProperty("agent", out var runtimeEntry).ShouldBeTrue();
        runtimeEntry.GetArrayLength().ShouldBe(2);

        // The actor's AddMemberAsync must not run when the inheritance
        // check rejects — otherwise the projection would persist a sub-
        // unit edge for a config the dispatch path would refuse later.
        await parentBProxy.DidNotReceive().AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "unit"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMember_SubunitTwoParentsDivergingAgentRuntime_ChildPinsRuntime_Returns204()
    {
        // Same parent topology as the conflict test, but the child unit
        // has its own agent runtime pinned. Per ADR-0039 §6, an explicit value
        // on the child suppresses inheritance for that field — the
        // assignment must succeed.
        var ct = TestContext.Current.CancellationToken;
        ResetState();

        ArrangeUnit(ParentAUuid, "parent-a");
        var parentBProxy = ArrangeUnit(ParentBUuid, "parent-b");
        ArrangeUnit(ChildUuid, "child");

        StubUnitDefaults(ParentAUuid, new UnitExecutionDefaults(Agent: "claude-code"));
        StubUnitDefaults(ParentBUuid, new UnitExecutionDefaults(Agent: "spring-voyage"));
        // Child pins agent runtime — the parent-disagreement is moot for that field.
        StubUnitDefaults(ChildUuid, new UnitExecutionDefaults(Agent: "claude-code"));

        await UpsertSubunitEdgeAsync(ParentAUuid, ChildUuid, ct);

        var body = new AddMemberRequest(new AddressDto("unit", ChildUuid.ToString("N")));
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/tenant/units/{ParentBUuid:N}/members")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        var response = await _client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await parentBProxy.Received(1).AddMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == ChildUuid),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveMember_SubunitNoRemainingParents_Returns204()
    {
        // ADR-0039 §6 / B6: removing the child's only parent leaves it
        // top-level. The remaining parent set is empty, so no
        // multi-parent conflict is possible and the endpoint accepts.
        var ct = TestContext.Current.CancellationToken;
        ResetState();

        var parentAProxy = ArrangeUnit(ParentAUuid, "parent-a");
        ArrangeUnit(ChildUuid, "child");

        await UpsertSubunitEdgeAsync(ParentAUuid, ChildUuid, ct);

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{ParentAUuid:N}/members/{ChildUuid:N}",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await parentAProxy.Received(1).RemoveMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == ChildUuid),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveMember_SubunitRemainingParentsConsistent_Returns204()
    {
        // Removing parent A leaves parent B + parent C. Both remaining
        // parents resolve the same inherited runtime, so the unassign
        // proceeds.
        var ct = TestContext.Current.CancellationToken;
        ResetState();

        var parentAProxy = ArrangeUnit(ParentAUuid, "parent-a");
        ArrangeUnit(ParentBUuid, "parent-b");
        ArrangeUnit(ParentCUuid, "parent-c");
        ArrangeUnit(ChildUuid, "child");

        StubUnitDefaults(ParentBUuid, new UnitExecutionDefaults(Agent: "claude-code"));
        StubUnitDefaults(ParentCUuid, new UnitExecutionDefaults(Agent: "claude-code"));
        StubUnitDefaults(ChildUuid, defaults: null);

        await UpsertSubunitEdgeAsync(ParentAUuid, ChildUuid, ct);
        await UpsertSubunitEdgeAsync(ParentBUuid, ChildUuid, ct);
        await UpsertSubunitEdgeAsync(ParentCUuid, ChildUuid, ct);

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{ParentAUuid:N}/members/{ChildUuid:N}",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await parentAProxy.Received(1).RemoveMemberAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == ChildUuid),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveMember_SubunitRemainingParentsDiverging_ChildInherits_Returns422()
    {
        // Removing parent A leaves parent B + parent C. Those remaining
        // parents disagree on the inherited runtime and the child has no
        // own execution block, so B6 rejects before actor state changes.
        var ct = TestContext.Current.CancellationToken;
        ResetState();

        var parentAProxy = ArrangeUnit(ParentAUuid, "parent-a");
        ArrangeUnit(ParentBUuid, "parent-b");
        ArrangeUnit(ParentCUuid, "parent-c");
        ArrangeUnit(ChildUuid, "child");

        StubUnitDefaults(ParentBUuid, new UnitExecutionDefaults(Agent: "claude-code"));
        StubUnitDefaults(ParentCUuid, new UnitExecutionDefaults(Agent: "spring-voyage"));
        StubUnitDefaults(ChildUuid, defaults: null);

        await UpsertSubunitEdgeAsync(ParentAUuid, ChildUuid, ct);
        await UpsertSubunitEdgeAsync(ParentBUuid, ChildUuid, ct);
        await UpsertSubunitEdgeAsync(ParentCUuid, ChildUuid, ct);

        var response = await _client.DeleteAsync(
            $"/api/v1/tenant/units/{ParentAUuid:N}/members/{ChildUuid:N}",
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, ct);
        problem.GetProperty("error").GetString().ShouldBe("MultiParentInheritanceConflict");
        var conflictingFields = problem.GetProperty("conflictingFields");
        conflictingFields.TryGetProperty("agent", out var runtimeEntry).ShouldBeTrue();
        runtimeEntry.GetArrayLength().ShouldBe(2);
        var values = runtimeEntry.EnumerateArray()
            .Select(e => e.GetProperty("value").GetString())
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
        values.ShouldBe(new[] { "claude-code", "spring-voyage" });

        await parentAProxy.DidNotReceive().RemoveMemberAsync(
            Arg.Any<Address>(),
            Arg.Any<CancellationToken>());
    }

    private void ResetState()
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();
        _factory.UnitExecutionStore.ClearReceivedCalls();

        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        _factory.UnitExecutionStore
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(null));

        // Tenant guard defaults to allow-all; tests that exercise the
        // cross-tenant path override this explicitly.
        _factory.TenantGuard.ClearReceivedCalls();
        _factory.TenantGuard
            .EnsureSameTenantAsync(Arg.Any<Address>(), Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Strip any sub-unit edge rows the previous test left behind so
        // the existing-parents lookup sees only what this test seeds.
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<Cvoya.Spring.Dapr.Data.SpringDbContext>();
        ctx.UnitSubunitMemberships.RemoveRange(ctx.UnitSubunitMemberships.ToList());
        ctx.SaveChanges();
    }

    private IUnitActor ArrangeUnit(Guid uuid, string displayName)
    {
        var entry = new DirectoryEntry(
            new Address("unit", uuid),
            uuid,
            displayName,
            $"{displayName} unit",
            null,
            DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == uuid),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var proxy = Substitute.For<IUnitActor>();
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == uuid.ToString("N")),
                Arg.Any<string>())
            .Returns(proxy);
        return proxy;
    }

    private void StubUnitDefaults(Guid unitId, UnitExecutionDefaults? defaults)
    {
        _factory.UnitExecutionStore
            .GetAsync(unitId.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(defaults));
    }

    private async Task UpsertSubunitEdgeAsync(Guid parentId, Guid childId, CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitSubunitMembershipRepository>();
        await repo.UpsertAsync(parentId, childId, ct);
    }
}
