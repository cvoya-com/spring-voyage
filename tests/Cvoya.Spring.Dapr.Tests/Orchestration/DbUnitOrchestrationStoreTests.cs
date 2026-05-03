// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using System.Text.Json;

using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Orchestration;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="DbUnitOrchestrationStore"/> (#606). Uses the EF
/// in-memory provider so we focus on the persistence / cache-invalidation
/// wiring — a full integration run through the caching decorator is
/// exercised by the end-to-end orchestration-endpoint tests.
/// </summary>
public class DbUnitOrchestrationStoreTests
{
    [Fact]
    public async Task GetStrategyKeyAsync_UnknownUnit_ReturnsNull()
    {
        var (store, _, _) = BuildStore();

        var key = await store.GetStrategyKeyAsync(
            "missing-unit", TestContext.Current.CancellationToken);

        key.ShouldBeNull();
    }

    [Fact]
    public async Task GetStrategyKeyAsync_StrategyPersisted_ReturnsIt()
    {
        var (store, scopeFactory, _) = BuildStore();
        var definition = JsonSerializer.SerializeToElement(new
        {
            orchestration = new { strategy = "workflow" },
        });
        await SeedUnitAsync(scopeFactory, unitId: "eng-team", definition: definition);

        var key = await store.GetStrategyKeyAsync(
            "eng-team", TestContext.Current.CancellationToken);

        key.ShouldBe("workflow");
    }

    [Fact]
    public async Task SetStrategyKeyAsync_NewRow_WritesSlotAndInvalidatesCache()
    {
        var (store, scopeFactory, invalidator) = BuildStore();
        await SeedUnitAsync(scopeFactory, unitId: "triage", actorId: "actor-triage", definition: null);

        await store.SetStrategyKeyAsync(
            "triage", "label-routed", TestContext.Current.CancellationToken);

        // Round-trip through GetStrategyKeyAsync so we exercise the JSON
        // shape the resolver will actually read.
        var key = await store.GetStrategyKeyAsync(
            "triage", TestContext.Current.CancellationToken);
        key.ShouldBe("label-routed");
        invalidator.Received().Invalidate("actor-triage");
    }

    [Fact]
    public async Task SetStrategyKeyAsync_PreservesOtherProperties()
    {
        var (store, scopeFactory, _) = BuildStore();
        var seedDefinition = JsonSerializer.SerializeToElement(new
        {
            expertise = new[] { new { domain = "triage" } },
            instructions = "Do the triage.",
        });
        await SeedUnitAsync(scopeFactory, unitId: "triage", definition: seedDefinition);

        await store.SetStrategyKeyAsync(
            "triage", "ai", TestContext.Current.CancellationToken);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var persisted = db.UnitDefinitions.Single(u => u.UnitId == "triage");
        var json = persisted.Definition!.Value;
        // Expertise + instructions survive the rewrite verbatim; the
        // orchestration slot carries the new key.
        json.TryGetProperty("expertise", out var expertise).ShouldBeTrue();
        expertise.GetArrayLength().ShouldBe(1);
        json.GetProperty("instructions").GetString().ShouldBe("Do the triage.");
        json.GetProperty("orchestration").GetProperty("strategy").GetString().ShouldBe("ai");
    }

    [Fact]
    public async Task SetStrategyKeyAsync_NullKey_StripsSlotButPreservesSiblings()
    {
        var (store, scopeFactory, invalidator) = BuildStore();
        var seedDefinition = JsonSerializer.SerializeToElement(new
        {
            expertise = new[] { new { domain = "triage" } },
            orchestration = new { strategy = "label-routed" },
        });
        await SeedUnitAsync(
            scopeFactory, unitId: "triage", actorId: "actor-triage", definition: seedDefinition);

        await store.SetStrategyKeyAsync(
            "triage", strategyKey: null, TestContext.Current.CancellationToken);

        var key = await store.GetStrategyKeyAsync(
            "triage", TestContext.Current.CancellationToken);
        key.ShouldBeNull();

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var persisted = db.UnitDefinitions.Single(u => u.UnitId == "triage");
        // Expertise survives the clear — only the orchestration slot is
        // stripped.
        persisted.Definition!.Value.TryGetProperty("expertise", out _).ShouldBeTrue();
        persisted.Definition.Value.TryGetProperty("orchestration", out _).ShouldBeFalse();
        invalidator.Received().Invalidate("actor-triage");
    }

    [Fact]
    public async Task SetStrategyKeyAsync_UnknownUnit_IsNoOp()
    {
        var (store, _, invalidator) = BuildStore();

        await store.SetStrategyKeyAsync(
            "ghost", "ai", TestContext.Current.CancellationToken);

        invalidator.DidNotReceive().Invalidate(Arg.Any<string>());
    }

    private static (DbUnitOrchestrationStore Store, IServiceScopeFactory ScopeFactory, IOrchestrationStrategyCacheInvalidator Invalidator) BuildStore()
    {
        var dbName = $"orch-store-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<SpringDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var invalidator = Substitute.For<IOrchestrationStrategyCacheInvalidator>();
        var store = new DbUnitOrchestrationStore(
            scopeFactory, invalidator, NullLoggerFactory.Instance);
        return (store, scopeFactory, invalidator);
    }

    private static async Task SeedUnitAsync(
        IServiceScopeFactory scopeFactory,
        string unitId,
        JsonElement? definition,
        string? actorId = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        JsonElement? stableDefinition = null;
        if (definition is { } el)
        {
            using var doc = JsonDocument.Parse(el.GetRawText());
            stableDefinition = doc.RootElement.Clone();
        }

        db.UnitDefinitions.Add(new UnitDefinitionEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = unitId,
            Description = "test",
            Definition = stableDefinition,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}