// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Data;

using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="EfAgentCloningPolicyRepository"/> (#2051 / ADR-0040).
/// Exercises round-trip, scope isolation, empty-policy delete, idempotent
/// delete, and tenant isolation against the EF in-memory provider — the
/// integration suite covers the same surface against Postgres. The tests
/// pin the contract <c>DefaultAgentCloningPolicyEnforcer</c> and the
/// HTTP endpoints rely on: <see cref="IAgentCloningPolicyRepository"/>
/// behaves identically to the pre-ADR state-store-backed shape.
/// </summary>
public class CloningPolicyRepositoryTests : IDisposable
{
    private static readonly Guid TenantA = new("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly Guid TenantB = new("bbbbbbbb-2222-2222-2222-000000000002");
    private static readonly Guid Agent_Ada = new("00000001-feed-1234-5678-000000000000");
    private static readonly Guid Agent_Other = new("00000002-feed-1234-5678-000000000000");

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly SpringDbContext _context;
    private readonly EfAgentCloningPolicyRepository _repository;

    public CloningPolicyRepositoryTests()
    {
        _context = NewContext(TenantA);
        _repository = new EfAgentCloningPolicyRepository(_context, NullLogger<EfAgentCloningPolicyRepository>.Instance);
    }

    [Fact]
    public async Task GetAsync_MissingAgentScope_ReturnsEmptyPolicy()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _repository.GetAsync(CloningPolicyScope.Agent, Agent_Ada.ToString("D"), ct);

        result.ShouldNotBeNull();
        result.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task GetAsync_MissingTenantScope_ReturnsEmptyPolicy()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await _repository.GetAsync(CloningPolicyScope.Tenant, TenantA.ToString("N"), ct);

        result.ShouldNotBeNull();
        result.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task SetAsync_AgentScope_RoundTripsAllSlots()
    {
        var ct = TestContext.Current.CancellationToken;
        var policy = new AgentCloningPolicy(
            AllowedPolicies: new[] { CloningPolicy.EphemeralNoMemory, CloningPolicy.EphemeralWithMemory },
            AllowedAttachmentModes: new[] { AttachmentMode.Detached },
            MaxClones: 5,
            MaxDepth: 1,
            Budget: 42m);

        await _repository.SetAsync(CloningPolicyScope.Agent, Agent_Ada.ToString("D"), policy, ct);

        var loaded = await _repository.GetAsync(CloningPolicyScope.Agent, Agent_Ada.ToString("D"), ct);

        loaded.AllowedPolicies.ShouldBe(policy.AllowedPolicies);
        loaded.AllowedAttachmentModes.ShouldBe(policy.AllowedAttachmentModes);
        loaded.MaxClones.ShouldBe(5);
        loaded.MaxDepth.ShouldBe(1);
        loaded.Budget.ShouldBe(42m);
    }

    [Fact]
    public async Task SetAsync_TenantScope_RoundTripsAndIsDistinctFromAgentScope()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantPolicy = new AgentCloningPolicy(MaxClones: 10);

        // Tenant scope: target id is ignored (the repository derives the
        // tenant from the ambient tenant context). Pass any non-empty
        // string to satisfy the input-validation contract.
        await _repository.SetAsync(CloningPolicyScope.Tenant, TenantA.ToString("N"), tenantPolicy, ct);

        // Agent scope for the same Guid should still be empty — distinct
        // (scope_type, scope_id) triples.
        var agentScope = await _repository.GetAsync(
            CloningPolicyScope.Agent, TenantA.ToString("D"), ct);
        agentScope.IsEmpty.ShouldBeTrue();

        var tenantScope = await _repository.GetAsync(CloningPolicyScope.Tenant, TenantA.ToString("N"), ct);
        tenantScope.MaxClones.ShouldBe(10);
    }

    [Fact]
    public async Task SetAsync_AgentScope_UpsertsInPlace()
    {
        // Re-setting a policy on the same agent must replace the existing
        // row's policy column rather than create a duplicate (the unique
        // index would prevent that, but we want to assert the upsert path
        // explicitly).
        var ct = TestContext.Current.CancellationToken;
        await _repository.SetAsync(
            CloningPolicyScope.Agent, Agent_Ada.ToString("D"),
            new AgentCloningPolicy(MaxClones: 3),
            ct);

        await _repository.SetAsync(
            CloningPolicyScope.Agent, Agent_Ada.ToString("D"),
            new AgentCloningPolicy(MaxClones: 7, Budget: 99m),
            ct);

        var loaded = await _repository.GetAsync(
            CloningPolicyScope.Agent, Agent_Ada.ToString("D"), ct);
        loaded.MaxClones.ShouldBe(7);
        loaded.Budget.ShouldBe(99m);

        // Exactly one row exists for the agent scope.
        var rowCount = await _context.CloningPolicies
            .CountAsync(e => e.ScopeType == CloningPolicyScopeType.Agent && e.ScopeId == Agent_Ada, ct);
        rowCount.ShouldBe(1);
    }

    [Fact]
    public async Task SetAsync_EmptyPolicy_DeletesTheRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.SetAsync(
            CloningPolicyScope.Agent, Agent_Ada.ToString("D"),
            new AgentCloningPolicy(MaxClones: 3),
            ct);

        await _repository.SetAsync(
            CloningPolicyScope.Agent, Agent_Ada.ToString("D"),
            AgentCloningPolicy.Empty,
            ct);

        // Empty persisted means "no row" — confirm the underlying table
        // actually has zero rows for this agent.
        var rowCount = await _context.CloningPolicies
            .CountAsync(e => e.ScopeType == CloningPolicyScopeType.Agent && e.ScopeId == Agent_Ada, ct);
        rowCount.ShouldBe(0);

        // GET still returns Empty from the absent-row branch.
        var loaded = await _repository.GetAsync(
            CloningPolicyScope.Agent, Agent_Ada.ToString("D"), ct);
        loaded.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteAsync_IsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange — nothing persisted.
        await _repository.DeleteAsync(CloningPolicyScope.Agent, Agent_Ada.ToString("D"), ct);

        // Act — again.
        await _repository.DeleteAsync(CloningPolicyScope.Agent, Agent_Ada.ToString("D"), ct);

        // Assert — no exception, GET still returns Empty.
        var loaded = await _repository.GetAsync(
            CloningPolicyScope.Agent, Agent_Ada.ToString("D"), ct);
        loaded.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteAsync_AgentScope_RemovesRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.SetAsync(
            CloningPolicyScope.Agent, Agent_Ada.ToString("D"),
            new AgentCloningPolicy(MaxClones: 3),
            ct);

        await _repository.DeleteAsync(CloningPolicyScope.Agent, Agent_Ada.ToString("D"), ct);

        var loaded = await _repository.GetAsync(
            CloningPolicyScope.Agent, Agent_Ada.ToString("D"), ct);
        loaded.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteAsync_TenantScope_RemovesRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await _repository.SetAsync(
            CloningPolicyScope.Tenant, TenantA.ToString("N"),
            new AgentCloningPolicy(MaxClones: 10),
            ct);

        await _repository.DeleteAsync(CloningPolicyScope.Tenant, TenantA.ToString("N"), ct);

        var loaded = await _repository.GetAsync(CloningPolicyScope.Tenant, TenantA.ToString("N"), ct);
        loaded.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task ScopeIsolation_AgentAndTenantPoliciesAreIndependent()
    {
        // The same target Guid string in two different scopes must resolve
        // to distinct rows: agent-scope queries by (agent, scope_id) and
        // tenant-scope queries by (tenant, NULL). A regression here would
        // mean the enforcer's "walk agent then tenant" composition would
        // confuse the two scopes.
        var ct = TestContext.Current.CancellationToken;

        await _repository.SetAsync(
            CloningPolicyScope.Agent, Agent_Ada.ToString("D"),
            new AgentCloningPolicy(MaxClones: 3),
            ct);
        await _repository.SetAsync(
            CloningPolicyScope.Tenant, TenantA.ToString("N"),
            new AgentCloningPolicy(MaxClones: 12),
            ct);

        var agent = await _repository.GetAsync(
            CloningPolicyScope.Agent, Agent_Ada.ToString("D"), ct);
        var tenant = await _repository.GetAsync(
            CloningPolicyScope.Tenant, TenantA.ToString("N"), ct);

        agent.MaxClones.ShouldBe(3);
        tenant.MaxClones.ShouldBe(12);
    }

    [Fact]
    public async Task PerAgent_DoesNotLeakAcrossAgents()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.SetAsync(
            CloningPolicyScope.Agent, Agent_Ada.ToString("D"),
            new AgentCloningPolicy(MaxClones: 3),
            ct);
        await _repository.SetAsync(
            CloningPolicyScope.Agent, Agent_Other.ToString("D"),
            new AgentCloningPolicy(MaxClones: 7),
            ct);

        var ada = await _repository.GetAsync(
            CloningPolicyScope.Agent, Agent_Ada.ToString("D"), ct);
        var other = await _repository.GetAsync(
            CloningPolicyScope.Agent, Agent_Other.ToString("D"), ct);

        ada.MaxClones.ShouldBe(3);
        other.MaxClones.ShouldBe(7);
    }

    [Fact]
    public async Task TenantIsolation_TenantBCannotSeeTenantARows()
    {
        // Tenant query filter is the gate: a write under TenantA must be
        // invisible to a TenantB context, and the TenantB row stays
        // distinct so cross-tenant reads cannot collapse to a single row.
        var ct = TestContext.Current.CancellationToken;

        await _repository.SetAsync(
            CloningPolicyScope.Tenant, TenantA.ToString("N"),
            new AgentCloningPolicy(MaxClones: 5),
            ct);

        await using var contextB = NewContext(TenantB);
        var repositoryB = new EfAgentCloningPolicyRepository(
            contextB, NullLogger<EfAgentCloningPolicyRepository>.Instance);

        var seen = await repositoryB.GetAsync(CloningPolicyScope.Tenant, TenantB.ToString("N"), ct);
        seen.IsEmpty.ShouldBeTrue();

        await repositoryB.SetAsync(
            CloningPolicyScope.Tenant, TenantB.ToString("N"),
            new AgentCloningPolicy(MaxClones: 99),
            ct);

        // TenantA still sees its own row; TenantB sees its own row.
        var aRow = await _repository.GetAsync(CloningPolicyScope.Tenant, TenantA.ToString("N"), ct);
        aRow.MaxClones.ShouldBe(5);

        var bRow = await repositoryB.GetAsync(CloningPolicyScope.Tenant, TenantB.ToString("N"), ct);
        bRow.MaxClones.ShouldBe(99);
    }

    [Fact]
    public async Task GetAsync_UnparseableAgentTargetId_ReturnsEmpty()
    {
        // Pre-ADR contract: GetAsync never throws for an unknown agent —
        // it returns Empty. The EF impl preserves that for malformed input
        // (the enforcer treats Empty as "no constraint", so a malformed id
        // never silently denies a clone request).
        var ct = TestContext.Current.CancellationToken;
        var loaded = await _repository.GetAsync(CloningPolicyScope.Agent, "not-a-guid", ct);
        loaded.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task SetAsync_UnparseableAgentTargetId_Throws()
    {
        // Writes are stricter than reads: a malformed agent id on SetAsync
        // is a programmer error (the endpoint resolves the agent first via
        // the directory). Refusing surfaces the bug at the call site rather
        // than silently no-op'ing.
        var ct = TestContext.Current.CancellationToken;
        await Should.ThrowAsync<ArgumentException>(async () =>
            await _repository.SetAsync(
                CloningPolicyScope.Agent, "not-a-guid",
                new AgentCloningPolicy(MaxClones: 3),
                ct));
    }

    [Fact]
    public async Task PolicySurvivesAcrossDbContextInstances()
    {
        // Cross-restart proxy: each repository instance gets its own
        // DbContext, simulating actor reactivation reading the same row.
        var ct = TestContext.Current.CancellationToken;

        await using (var write = NewContext(TenantA))
        {
            var writer = new EfAgentCloningPolicyRepository(
                write, NullLogger<EfAgentCloningPolicyRepository>.Instance);
            await writer.SetAsync(
                CloningPolicyScope.Agent, Agent_Ada.ToString("D"),
                new AgentCloningPolicy(MaxClones: 4, Budget: 12.5m),
                ct);
        }

        await using (var read = NewContext(TenantA))
        {
            var reader = new EfAgentCloningPolicyRepository(
                read, NullLogger<EfAgentCloningPolicyRepository>.Instance);
            var loaded = await reader.GetAsync(
                CloningPolicyScope.Agent, Agent_Ada.ToString("D"), ct);
            loaded.MaxClones.ShouldBe(4);
            loaded.Budget.ShouldBe(12.5m);
        }
    }

    private SpringDbContext NewContext(Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: _dbName)
            .Options;

        return new SpringDbContext(options, new StaticTenantContext(tenantId));
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
