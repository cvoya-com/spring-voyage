// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

public class BudgetEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BudgetEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<BudgetLimitEntity?> ReadAsync(string scopeType, Guid? scopeId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        return await db.BudgetLimits
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.ScopeType == scopeType && b.ScopeId == scopeId);
    }

    private async Task SeedAsync(string scopeType, Guid? scopeId, decimal dailyBudget)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.BudgetLimits.Add(new BudgetLimitEntity
        {
            Id = Guid.NewGuid(),
            TenantId = OssTenantIds.Default,
            ScopeType = scopeType,
            ScopeId = scopeId,
            DailyBudget = dailyBudget,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static string FreshGuidPath() => GuidFormatter.Format(Guid.NewGuid());

    [Fact]
    public async Task SetAgentBudget_ValidRequest_ReturnsBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentPath = FreshGuidPath();
        GuidFormatter.TryParse(agentPath, out var agentGuid).ShouldBeTrue();

        var request = new SetBudgetRequest(25.50m);

        var response = await _client.PutAsJsonAsync($"/api/v1/tenant/agents/{agentPath}/budget", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var budget = await response.Content.ReadFromJsonAsync<BudgetResponse>(ct);
        budget.ShouldNotBeNull();
        budget!.DailyBudget.ShouldBe(25.50m);

        var row = await ReadAsync(BudgetLimitScope.Agent, agentGuid);
        row.ShouldNotBeNull();
        row!.DailyBudget.ShouldBe(25.50m);
        row.TenantId.ShouldBe(OssTenantIds.Default);
    }

    [Fact]
    public async Task SetAgentBudget_ZeroBudget_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new SetBudgetRequest(0m);

        var response = await _client.PutAsJsonAsync($"/api/v1/tenant/agents/{FreshGuidPath()}/budget", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetAgentBudget_NegativeBudget_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new SetBudgetRequest(-5m);

        var response = await _client.PutAsJsonAsync($"/api/v1/tenant/agents/{FreshGuidPath()}/budget", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetAgentBudget_UnparseableId_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new SetBudgetRequest(10m);

        var response = await _client.PutAsJsonAsync("/api/v1/tenant/agents/not-a-guid/budget", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetAgentBudget_ExistingRow_UpsertsNewValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentGuid = Guid.NewGuid();
        var agentPath = GuidFormatter.Format(agentGuid);

        await SeedAsync(BudgetLimitScope.Agent, agentGuid, 5.0m);

        var response = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/agents/{agentPath}/budget",
            new SetBudgetRequest(99.0m),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var row = await ReadAsync(BudgetLimitScope.Agent, agentGuid);
        row.ShouldNotBeNull();
        row!.DailyBudget.ShouldBe(99.0m);
    }

    [Fact]
    public async Task GetAgentBudget_BudgetExists_ReturnsBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentGuid = Guid.NewGuid();
        var agentPath = GuidFormatter.Format(agentGuid);

        await SeedAsync(BudgetLimitScope.Agent, agentGuid, 10.0m);

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{agentPath}/budget", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var budget = await response.Content.ReadFromJsonAsync<BudgetResponse>(ct);
        budget.ShouldNotBeNull();
        budget!.DailyBudget.ShouldBe(10.0m);
    }

    [Fact]
    public async Task GetAgentBudget_NoBudget_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{FreshGuidPath()}/budget", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SetTenantBudget_ValidRequest_ReturnsBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new SetBudgetRequest(100.0m);

        var response = await _client.PutAsJsonAsync("/api/v1/tenant/budget", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var budget = await response.Content.ReadFromJsonAsync<BudgetResponse>(ct);
        budget.ShouldNotBeNull();
        budget!.DailyBudget.ShouldBe(100.0m);

        var row = await ReadAsync(BudgetLimitScope.Tenant, scopeId: null);
        row.ShouldNotBeNull();
        row!.DailyBudget.ShouldBe(100.0m);
        row.TenantId.ShouldBe(OssTenantIds.Default);
    }

    [Fact]
    public async Task SetTenantBudget_Twice_UpsertsSingleRow()
    {
        var ct = TestContext.Current.CancellationToken;

        // Two writes back-to-back must collapse into a single row — the
        // partial unique index on (tenant_id, scope_type) WHERE scope_id IS
        // NULL is the gate. (Verifies the upsert path.)
        var first = await _client.PutAsJsonAsync("/api/v1/tenant/budget", new SetBudgetRequest(50.0m), ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await _client.PutAsJsonAsync("/api/v1/tenant/budget", new SetBudgetRequest(75.0m), ct);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var rows = await db.BudgetLimits
            .AsNoTracking()
            .Where(b => b.ScopeType == BudgetLimitScope.Tenant && b.ScopeId == null)
            .ToListAsync(ct);

        rows.Count.ShouldBe(1);
        rows[0].DailyBudget.ShouldBe(75.0m);
    }

    [Fact]
    public async Task GetTenantBudget_BudgetExists_ReturnsBudget()
    {
        var ct = TestContext.Current.CancellationToken;

        // Tests in this fixture share an in-memory DB instance, so other
        // tests may have left a tenant-scope row behind. Reset to a known
        // value before exercising the read.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var existing = await db.BudgetLimits
                .Where(b => b.ScopeType == BudgetLimitScope.Tenant && b.ScopeId == null)
                .ToListAsync(ct);
            db.BudgetLimits.RemoveRange(existing);
            await db.SaveChangesAsync(ct);
        }
        await SeedAsync(BudgetLimitScope.Tenant, scopeId: null, 50.0m);

        var response = await _client.GetAsync("/api/v1/tenant/budget", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var budget = await response.Content.ReadFromJsonAsync<BudgetResponse>(ct);
        budget.ShouldNotBeNull();
        budget!.DailyBudget.ShouldBe(50.0m);
    }

    [Fact]
    public async Task GetTenantBudget_NoBudget_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;

        // Make sure no tenant row exists in the shared in-memory DB. The
        // shared CustomWebApplicationFactory test DB persists across tests
        // in the same class, so explicitly clear the row first.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var existing = await db.BudgetLimits
                .Where(b => b.ScopeType == BudgetLimitScope.Tenant && b.ScopeId == null)
                .ToListAsync(ct);
            db.BudgetLimits.RemoveRange(existing);
            await db.SaveChangesAsync(ct);
        }

        var response = await _client.GetAsync("/api/v1/tenant/budget", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // --- PR-C3 / #459: unit budgets ---------------------------------------

    [Fact]
    public async Task SetUnitBudget_ValidRequest_PersistsDailyBudgetUnderUnitScope()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitGuid = Guid.NewGuid();
        var unitPath = GuidFormatter.Format(unitGuid);

        var request = new SetBudgetRequest(30.00m);

        var response = await _client.PutAsJsonAsync($"/api/v1/tenant/units/{unitPath}/budget", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var budget = await response.Content.ReadFromJsonAsync<BudgetResponse>(ct);
        budget.ShouldNotBeNull();
        budget!.DailyBudget.ShouldBe(30.00m);

        var row = await ReadAsync(BudgetLimitScope.Unit, unitGuid);
        row.ShouldNotBeNull();
        row!.DailyBudget.ShouldBe(30.00m);
        row.TenantId.ShouldBe(OssTenantIds.Default);
    }

    [Fact]
    public async Task SetUnitBudget_ZeroBudget_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new SetBudgetRequest(0m);

        var response = await _client.PutAsJsonAsync($"/api/v1/tenant/units/{FreshGuidPath()}/budget", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetUnitBudget_UnparseableId_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new SetBudgetRequest(10m);

        var response = await _client.PutAsJsonAsync("/api/v1/tenant/units/not-a-guid/budget", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetUnitBudget_BudgetExists_ReturnsBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitGuid = Guid.NewGuid();
        var unitPath = GuidFormatter.Format(unitGuid);
        await SeedAsync(BudgetLimitScope.Unit, unitGuid, 12.5m);

        var response = await _client.GetAsync($"/api/v1/tenant/units/{unitPath}/budget", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var budget = await response.Content.ReadFromJsonAsync<BudgetResponse>(ct);
        budget.ShouldNotBeNull();
        budget!.DailyBudget.ShouldBe(12.5m);
    }

    [Fact]
    public async Task GetUnitBudget_NoBudget_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync($"/api/v1/tenant/units/{FreshGuidPath()}/budget", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // --- Tenant isolation: ADR-0040 multi-tenancy invariant ---------------

    [Fact]
    public async Task GetAgentBudget_RowOwnedByOtherTenant_NotVisible()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentGuid = Guid.NewGuid();
        var agentPath = GuidFormatter.Format(agentGuid);

        // Seed a row owned by a *different* tenant. With the EF query filter
        // bound to ITenantContext.CurrentTenantId == OssTenantIds.Default the
        // GET must not surface this row.
        var otherTenant = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.BudgetLimits.Add(new BudgetLimitEntity
            {
                Id = Guid.NewGuid(),
                TenantId = otherTenant,
                ScopeType = BudgetLimitScope.Agent,
                ScopeId = agentGuid,
                DailyBudget = 99.0m,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            // The auto-tenant-stamp in SaveChanges only acts when TenantId
            // is Guid.Empty; we explicitly set a foreign tenant so the row
            // is owned by `otherTenant`, not the ambient one.
            await db.SaveChangesAsync(ct);
        }

        var response = await _client.GetAsync($"/api/v1/tenant/agents/{agentPath}/budget", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
