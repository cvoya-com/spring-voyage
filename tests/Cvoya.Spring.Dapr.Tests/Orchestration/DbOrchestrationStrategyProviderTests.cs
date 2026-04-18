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
/// Tests for <see cref="DbOrchestrationStrategyProvider"/> (#491, tightened
/// in #519). Uses the EF in-memory provider so we stay focused on the lookup
/// predicate and JSON extraction — the same shape
/// <c>UnitCreationService</c> writes at manifest ingestion.
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
        await SeedUnitAsync(scopeFactory, actorId: "actor-bare", definition: null);

        var key = await provider.GetStrategyKeyAsync(
            "actor-bare", TestContext.Current.CancellationToken);

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
        await SeedUnitAsync(scopeFactory, actorId: "actor-no-orch", definition: definition);

        var key = await provider.GetStrategyKeyAsync(
            "actor-no-orch", TestContext.Current.CancellationToken);

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
            actorId: "actor-deleted",
            definition: null,
            deletedAt: DateTimeOffset.UtcNow);

        var key = await provider.GetStrategyKeyAsync(
            "actor-deleted", TestContext.Current.CancellationToken);

        key.ShouldBeNull();
    }

    [Fact]
    public async Task GetStrategyKeyAsync_MatchesOnActorId()
    {
        // The sole production caller (`DefaultOrchestrationStrategyResolver`
        // via `UnitActor`) passes `Id.GetId()` — the Dapr actor id. The
        // provider must return the manifest-declared key when looking up by
        // that id.
        var (provider, scopeFactory) = BuildProvider();
        var definition = JsonSerializer.SerializeToElement(new
        {
            orchestration = new { strategy = "label-routed" },
        });
        await SeedUnitAsync(
            scopeFactory,
            unitId: "triage",
            actorId: "actor-triage",
            definition: definition);

        var key = await provider.GetStrategyKeyAsync(
            "actor-triage", TestContext.Current.CancellationToken);

        key.ShouldBe("label-routed");
    }

    [Fact]
    public async Task GetStrategyKeyAsync_ByUserFacingUnitId_DoesNotMatch()
    {
        // #519: the OR-match on UnitId was tightened to ActorId-only. A
        // caller that passes the user-facing unit name must no longer hit
        // the row — the resolver will fall back to the unkeyed default,
        // which is the desired failure mode to surface a mis-typed caller.
        var (provider, scopeFactory) = BuildProvider();
        var definition = JsonSerializer.SerializeToElement(new
        {
            orchestration = new { strategy = "label-routed" },
        });
        await SeedUnitAsync(
            scopeFactory,
            unitId: "triage",
            actorId: "actor-triage",
            definition: definition);

        var key = await provider.GetStrategyKeyAsync(
            "triage", TestContext.Current.CancellationToken);

        key.ShouldBeNull();
    }

    [Fact]
    public async Task GetStrategyKeyAsync_GuidCollisionOnAnotherUnitsUnitId_DoesNotMisMatch()
    {
        // #519 latent-collision case: row A's ActorId happens to equal the
        // lookup key, and row B's UnitId *also* equals the lookup key (e.g.
        // a future host allowing GUID-shaped unit names). The tightened
        // provider must return A's strategy — never B's — regardless of
        // persistence order or B's strategy value.
        var (provider, scopeFactory) = BuildProvider();
        var collidingId = Guid.NewGuid().ToString();

        await SeedUnitAsync(
            scopeFactory,
            unitId: "unit-a",
            actorId: collidingId,
            definition: JsonSerializer.SerializeToElement(new
            {
                orchestration = new { strategy = "workflow" },
            }));

        await SeedUnitAsync(
            scopeFactory,
            unitId: collidingId,
            actorId: "actor-b",
            definition: JsonSerializer.SerializeToElement(new
            {
                orchestration = new { strategy = "label-routed" },
            }));

        var key = await provider.GetStrategyKeyAsync(
            collidingId, TestContext.Current.CancellationToken);

        key.ShouldBe("workflow");
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
        // Capture the DB name in a local so the options builder callback — which
        // may fire once per scope when options is Scoped — resolves to the same
        // name every time. Interpolating Guid.NewGuid() inside the callback
        // silently gave every scope its own empty in-memory database.
        var dbName = $"orch-strategy-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<SpringDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var provider = new DbOrchestrationStrategyProvider(scopeFactory, NullLoggerFactory.Instance);
        return (provider, scopeFactory);
    }

    private static async Task SeedUnitAsync(
        IServiceScopeFactory scopeFactory,
        JsonElement? definition,
        string? unitId = null,
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

        var resolvedActorId = actorId ?? Guid.NewGuid().ToString();
        db.UnitDefinitions.Add(new UnitDefinitionEntity
        {
            Id = Guid.NewGuid(),
            UnitId = unitId ?? resolvedActorId,
            ActorId = resolvedActorId,
            Name = unitId ?? resolvedActorId,
            Description = "test",
            Definition = stableDefinition,
            DeletedAt = deletedAt,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}