// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using System.Text.Json;

using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Orchestration;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="DbOrchestrationStrategyProvider"/> (#491). Uses the
/// EF in-memory provider so we stay focused on the JSON extraction logic —
/// the same shape <c>UnitCreationService</c> writes at manifest ingestion.
/// </summary>
public class DbOrchestrationStrategyProviderTests
{
    [Fact]
    public async Task GetStrategyKeyAsync_NoEntity_ReturnsNull()
    {
        var (provider, _) = BuildProvider();

        var key = await provider.GetStrategyKeyAsync(
            "missing-unit", TestContext.Current.CancellationToken);

        key.ShouldBeNull();
    }

    [Fact]
    public async Task GetStrategyKeyAsync_EntityWithNoDefinition_ReturnsNull()
    {
        var (provider, scopeFactory) = BuildProvider();
        await SeedUnitAsync(scopeFactory, unitId: "bare-unit", definition: null);

        var key = await provider.GetStrategyKeyAsync(
            "bare-unit", TestContext.Current.CancellationToken);

        key.ShouldBeNull();
    }

    [Fact]
    public async Task GetStrategyKeyAsync_NoOrchestrationBlock_ReturnsNull()
    {
        var (provider, scopeFactory) = BuildProvider();
        var definition = JsonSerializer.SerializeToElement(new
        {
            expertise = Array.Empty<object>(),
        });
        await SeedUnitAsync(scopeFactory, unitId: "no-orchestration", definition: definition);

        var key = await provider.GetStrategyKeyAsync(
            "no-orchestration", TestContext.Current.CancellationToken);

        key.ShouldBeNull();
    }

    [Fact]
    public async Task GetStrategyKeyAsync_SoftDeletedEntity_ReturnsNull()
    {
        // Query filter drops soft-deleted rows — the provider must never
        // surface a strategy key from a deleted unit, regardless of JSON.
        var (provider, scopeFactory) = BuildProvider();
        await SeedUnitAsync(
            scopeFactory,
            unitId: "deleted-unit",
            definition: null,
            deletedAt: DateTimeOffset.UtcNow);

        var key = await provider.GetStrategyKeyAsync(
            "deleted-unit", TestContext.Current.CancellationToken);

        key.ShouldBeNull();
    }


    [Fact]
    public void ExtractStrategyKey_TrimsValue()
    {
        var definition = JsonSerializer.SerializeToElement(new
        {
            orchestration = new { strategy = "  label-routed  " },
        });

        DbOrchestrationStrategyProvider.ExtractStrategyKey(definition).ShouldBe("label-routed");
    }

    [Fact]
    public void ExtractStrategyKey_NonObjectOrchestration_ReturnsNull()
    {
        var definition = JsonSerializer.SerializeToElement(new
        {
            orchestration = "not-an-object",
        });

        DbOrchestrationStrategyProvider.ExtractStrategyKey(definition).ShouldBeNull();
    }

    private static (DbOrchestrationStrategyProvider Provider, IServiceScopeFactory ScopeFactory) BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<SpringDbContext>(opt => opt.UseInMemoryDatabase(
            $"orch-strategy-{Guid.NewGuid():N}"));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var provider = new DbOrchestrationStrategyProvider(scopeFactory, NullLoggerFactory.Instance);
        return (provider, scopeFactory);
    }

    private static async Task SeedUnitAsync(
        IServiceScopeFactory scopeFactory,
        string unitId,
        JsonElement? definition,
        string? actorId = null,
        DateTimeOffset? deletedAt = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        // Round-trip through string so the persisted JsonElement is backed
        // by an independent document rather than a JsonDocument that may be
        // disposed before the test re-reads it through EF InMemory.
        JsonElement? stableDefinition = null;
        if (definition is { } el)
        {
            using var doc = JsonDocument.Parse(el.GetRawText());
            stableDefinition = doc.RootElement.Clone();
        }

        db.UnitDefinitions.Add(new UnitDefinitionEntity
        {
            Id = Guid.NewGuid(),
            UnitId = unitId,
            ActorId = actorId ?? Guid.NewGuid().ToString(),
            Name = unitId,
            Description = "test",
            Definition = stableDefinition,
            DeletedAt = deletedAt,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}