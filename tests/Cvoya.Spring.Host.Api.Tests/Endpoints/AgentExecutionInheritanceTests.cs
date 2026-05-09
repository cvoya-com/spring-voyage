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
using Cvoya.Spring.Host.Api.Models;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for ADR-0039 §6 / B2: the
/// <c>PUT /api/v1/tenant/agents/{id}/execution</c> handler runs
/// multi-parent inheritance resolution against the patched view of the
/// agent's own config plus the current parent set, and rejects with a
/// structured 422 when any inheritable field diverges across parents.
/// </summary>
/// <remarks>
/// The two acceptance cases pinned by the task plan (units-are-agents §B2):
/// <list type="bullet">
///   <item>Agent with two diverging parent units, the field left to inherit
///   on the patch — 422 <c>MultiParentInheritanceConflict</c>.</item>
///   <item>Same parent set, but the conflicting field set explicitly on
///   the agent — 200, the value persists.</item>
/// </list>
/// </remarks>
public class AgentExecutionInheritanceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AgentExecutionInheritanceTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();

        // Fixture singletons accumulate received calls / configured returns
        // across tests; reset the substitutes the resolver consults so each
        // test sees a clean slate.
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.AgentExecutionStore.ClearReceivedCalls();
        _factory.UnitExecutionStore.ClearReceivedCalls();
        // Default the unit-execution store back to "no defaults" so a stale
        // arrangement from another fixture-sharing test does not leak in.
        _factory.UnitExecutionStore
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(null));
        _factory.AgentExecutionStore
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentExecutionShape?>(null));

        ClearMemberships();
    }

    [Fact]
    public async Task PutAgentExecution_DivergingParents_FieldLeftToInherit_Returns422()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentGuid = Guid.NewGuid();
        var unitA = Guid.NewGuid();
        var unitB = Guid.NewGuid();

        ArrangeAgentDirectoryEntry(agentGuid);
        ArrangeUnitDefaults(unitA, runtime: "claude-code");
        ArrangeUnitDefaults(unitB, runtime: "spring-voyage");
        await SeedMembershipsAsync(agentGuid, unitA, unitB, ct);

        // Patch carries `image` only — `runtime` (catalogue id, persisted as
        // `agent` on the shape) is left to inherit. The two parents
        // disagree, so the resolver must surface the conflict and the
        // endpoint must return 422 without writing anything.
        var response = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/agents/{agentGuid:N}/execution",
            new AgentExecutionResponse(
                Image: "ghcr.io/example/agent:latest",
                Runtime: null,
                Hosting: "ephemeral"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("error").GetString().ShouldBe("MultiParentInheritanceConflict");

        var conflictingFields = root.GetProperty("conflictingFields");
        conflictingFields.ValueKind.ShouldBe(JsonValueKind.Object);
        // The diverging slot on the persisted shape is `agent` (the
        // runtime catalogue id) — see ExecutionConfigInheritanceResolver
        // FieldNames.
        conflictingFields.TryGetProperty("agent", out var agentConflict).ShouldBeTrue();
        agentConflict.ValueKind.ShouldBe(JsonValueKind.Array);
        agentConflict.GetArrayLength().ShouldBe(2);

        // Each entry is a {source, value} pair; source is the parent unit
        // id rendered in 32-char no-dash hex.
        var values = new List<(string Source, string Value)>();
        foreach (var entry in agentConflict.EnumerateArray())
        {
            values.Add((
                entry.GetProperty("source").GetString()!,
                entry.GetProperty("value").GetString()!));
        }

        values.ShouldContain((unitA.ToString("N"), "claude-code"));
        values.ShouldContain((unitB.ToString("N"), "spring-voyage"));

        // Nothing was persisted on the rejection path.
        await _factory.AgentExecutionStore.DidNotReceive()
            .SetAsync(Arg.Any<string>(), Arg.Any<AgentExecutionShape>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PutAgentExecution_DivergingParents_FieldSetExplicitlyOnAgent_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentGuid = Guid.NewGuid();
        var unitA = Guid.NewGuid();
        var unitB = Guid.NewGuid();

        ArrangeAgentDirectoryEntry(agentGuid);
        ArrangeUnitDefaults(unitA, runtime: "claude-code");
        ArrangeUnitDefaults(unitB, runtime: "spring-voyage");
        await SeedMembershipsAsync(agentGuid, unitA, unitB, ct);

        // The post-patch view returned by the store mirrors what the
        // partial-update merge will end up persisting. We arrange the
        // post-write read here so the response shape on the 200 path is
        // deterministic (the resolver pre-check above runs against
        // existing+patch in memory, not against this stub).
        _factory.AgentExecutionStore
            .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentExecutionShape?>(
                new AgentExecutionShape(
                    Image: "ghcr.io/example/agent:latest",
                    Hosting: "ephemeral",
                    Agent: "spring-voyage")));

        // Operator sets `runtime` explicitly on the patch — the per-parent
        // disagreement is moot for that field (resolver § "agent value
        // wins"), so the call must succeed and the store must be written.
        var response = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/agents/{agentGuid:N}/execution",
            new AgentExecutionResponse(
                Image: "ghcr.io/example/agent:latest",
                Runtime: "spring-voyage",
                Hosting: "ephemeral"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<AgentExecutionResponse>(cancellationToken: ct);
        payload.ShouldNotBeNull();
        payload.Runtime.ShouldBe("spring-voyage");

        // Store WAS written on the success path.
        await _factory.AgentExecutionStore.Received(1)
            .SetAsync(Arg.Any<string>(), Arg.Any<AgentExecutionShape>(), Arg.Any<CancellationToken>());
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private void ArrangeAgentDirectoryEntry(Guid agentGuid)
    {
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == Address.AgentScheme && a.Id == agentGuid),
                Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address(Address.AgentScheme, agentGuid),
                agentGuid,
                "Test Agent",
                "Test agent",
                null,
                DateTimeOffset.UtcNow));
    }

    private void ArrangeUnitDefaults(Guid unitGuid, string runtime)
    {
        // The resolver formats parent unit ids through GuidFormatter (no-dash
        // hex) before calling IUnitExecutionStore.GetAsync. Match by the
        // formatted form so the substitute returns the right defaults for
        // each unit.
        _factory.UnitExecutionStore
            .GetAsync(unitGuid.ToString("N"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<UnitExecutionDefaults?>(
                new UnitExecutionDefaults(Agent: runtime)));
    }

    private async Task SeedMembershipsAsync(Guid agentGuid, Guid unitA, Guid unitB, CancellationToken ct)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
        await repo.UpsertAsync(new UnitMembership(unitA, agentGuid, Enabled: true), ct);
        await repo.UpsertAsync(new UnitMembership(unitB, agentGuid, Enabled: true), ct);
    }

    private void ClearMemberships()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<Cvoya.Spring.Dapr.Data.SpringDbContext>();
        ctx.UnitMemberships.RemoveRange(ctx.UnitMemberships.ToList());
        ctx.SaveChanges();
    }
}
