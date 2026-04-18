// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Capabilities;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Dapr.Capabilities;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="DbExpertiseSeedProvider.ExtractExpertise"/> — the pure
/// JSON-to-<see cref="ExpertiseDomain"/> projection — and the DB lookup path
/// tightened under #519 to match on <c>ActorId</c> alone. Integration tests
/// further round-trip the provider through <c>OnActivateAsync</c>. See #488.
/// </summary>
public class DbExpertiseSeedProviderTests
{
    [Fact]
    public void ExtractExpertise_Null_ReturnsNull()
    {
        DbExpertiseSeedProvider.ExtractExpertise(null).ShouldBeNull();
    }

    [Fact]
    public void ExtractExpertise_NoExpertiseProperty_ReturnsNull()
    {
        var doc = JsonSerializer.SerializeToElement(new { instructions = "do things" });
        DbExpertiseSeedProvider.ExtractExpertise(doc).ShouldBeNull();
    }

    [Fact]
    public void ExtractExpertise_EmptyArray_ReturnsEmpty()
    {
        var doc = JsonSerializer.SerializeToElement(new { expertise = Array.Empty<object>() });
        var result = DbExpertiseSeedProvider.ExtractExpertise(doc);
        result.ShouldNotBeNull();
        result!.Count.ShouldBe(0);
    }

    [Fact]
    public void ExtractExpertise_DomainAndLevel_MapsCorrectly()
    {
        // Mirrors the user-facing YAML grammar: `- domain: X\n  level: expert`.
        var doc = JsonSerializer.SerializeToElement(new
        {
            expertise = new[]
            {
                new { domain = "python/fastapi", level = "expert" },
                new { domain = "react/nextjs", level = "advanced" },
            },
        });

        var result = DbExpertiseSeedProvider.ExtractExpertise(doc)!;

        result.Count.ShouldBe(2);
        result[0].Name.ShouldBe("python/fastapi");
        result[0].Level.ShouldBe(ExpertiseLevel.Expert);
        result[1].Name.ShouldBe("react/nextjs");
        result[1].Level.ShouldBe(ExpertiseLevel.Advanced);
    }

    [Fact]
    public void ExtractExpertise_NameKey_AlsoAccepted()
    {
        // Wire-shape key spelling (`name`) must round-trip too so a dump from
        // GET /api/v1/agents/{id}/expertise can be replayed through a
        // definition file.
        var doc = JsonSerializer.SerializeToElement(new
        {
            expertise = new[]
            {
                new { name = "architecture", description = "system design", level = "expert" },
            },
        });

        var result = DbExpertiseSeedProvider.ExtractExpertise(doc)!;

        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("architecture");
        result[0].Description.ShouldBe("system design");
        result[0].Level.ShouldBe(ExpertiseLevel.Expert);
    }

    [Fact]
    public void ExtractExpertise_MissingLevel_YieldsNullLevel()
    {
        var doc = JsonSerializer.SerializeToElement(new
        {
            expertise = new[] { new { domain = "coding" } },
        });

        var result = DbExpertiseSeedProvider.ExtractExpertise(doc)!;
        result[0].Level.ShouldBeNull();
    }

    [Fact]
    public void ExtractExpertise_UnknownLevel_IgnoresLevel()
    {
        var doc = JsonSerializer.SerializeToElement(new
        {
            expertise = new[] { new { domain = "coding", level = "wizard" } },
        });

        var result = DbExpertiseSeedProvider.ExtractExpertise(doc)!;
        result.Count.ShouldBe(1);
        result[0].Level.ShouldBeNull();
    }

    [Fact]
    public void ExtractExpertise_BlankDomain_SkipsEntry()
    {
        var doc = JsonSerializer.SerializeToElement(new
        {
            expertise = new object[]
            {
                new { domain = "", level = "expert" },
                new { domain = "ok", level = "expert" },
            },
        });

        var result = DbExpertiseSeedProvider.ExtractExpertise(doc)!;
        result.Count.ShouldBe(1);
        result[0].Name.ShouldBe("ok");
    }

    [Fact]
    public void ExtractExpertise_NonArrayExpertise_ReturnsNull()
    {
        var doc = JsonSerializer.SerializeToElement(new { expertise = "not-an-array" });
        DbExpertiseSeedProvider.ExtractExpertise(doc).ShouldBeNull();
    }

    // -------------------------------------------------------------------
    // DB lookup predicate (#519)
    // -------------------------------------------------------------------

    [Fact]
    public async Task GetAgentSeedAsync_MatchesOnActorId()
    {
        // Production caller (`AgentActor.OnActivateAsync`) passes
        // `Id.GetId()` — the Dapr actor id. The provider must return the
        // seed when looking up by that id.
        var (provider, scopeFactory) = BuildProvider();
        var definition = JsonSerializer.SerializeToElement(new
        {
            expertise = new[] { new { domain = "python", level = "expert" } },
        });
        await SeedAgentAsync(
            scopeFactory, agentId: "ada", actorId: "actor-ada", definition: definition);

        var result = await provider.GetAgentSeedAsync(
            "actor-ada", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Count.ShouldBe(1);
        result[0].Name.ShouldBe("python");
    }

    [Fact]
    public async Task GetAgentSeedAsync_ByUserFacingAgentId_DoesNotMatch()
    {
        // #519: matching was tightened to ActorId-only. A caller that
        // passes the user-facing agent name no longer hits the row.
        var (provider, scopeFactory) = BuildProvider();
        var definition = JsonSerializer.SerializeToElement(new
        {
            expertise = new[] { new { domain = "python", level = "expert" } },
        });
        await SeedAgentAsync(
            scopeFactory, agentId: "ada", actorId: "actor-ada", definition: definition);

        var result = await provider.GetAgentSeedAsync(
            "ada", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAgentSeedAsync_GuidCollisionOnAnotherAgentsAgentId_DoesNotMisMatch()
    {
        // #519 latent-collision case: row A's ActorId equals the lookup key,
        // and row B's AgentId also equals the lookup key. The tightened
        // provider must return A's seed — never B's.
        var (provider, scopeFactory) = BuildProvider();
        var collidingId = Guid.NewGuid().ToString();

        await SeedAgentAsync(
            scopeFactory,
            agentId: "agent-a",
            actorId: collidingId,
            definition: JsonSerializer.SerializeToElement(new
            {
                expertise = new[] { new { domain = "a-domain", level = "expert" } },
            }));

        await SeedAgentAsync(
            scopeFactory,
            agentId: collidingId,
            actorId: "actor-b",
            definition: JsonSerializer.SerializeToElement(new
            {
                expertise = new[] { new { domain = "b-domain", level = "novice" } },
            }));

        var result = await provider.GetAgentSeedAsync(
            collidingId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Count.ShouldBe(1);
        result[0].Name.ShouldBe("a-domain");
    }

    [Fact]
    public async Task GetAgentSeedAsync_SoftDeleted_ReturnsNull()
    {
        var (provider, scopeFactory) = BuildProvider();
        var definition = JsonSerializer.SerializeToElement(new
        {
            expertise = new[] { new { domain = "python", level = "expert" } },
        });
        await SeedAgentAsync(
            scopeFactory,
            agentId: "ada",
            actorId: "actor-ada-deleted",
            definition: definition,
            deletedAt: DateTimeOffset.UtcNow);

        var result = await provider.GetAgentSeedAsync(
            "actor-ada-deleted", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetUnitSeedAsync_MatchesOnActorId()
    {
        // Production caller (`UnitActor.OnActivateAsync`) passes
        // `Id.GetId()` — the Dapr actor id.
        var (provider, scopeFactory) = BuildProvider();
        var definition = JsonSerializer.SerializeToElement(new
        {
            expertise = new[] { new { domain = "triage", level = "expert" } },
        });
        await SeedUnitAsync(
            scopeFactory, unitId: "triage", actorId: "actor-triage", definition: definition);

        var result = await provider.GetUnitSeedAsync(
            "actor-triage", TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Count.ShouldBe(1);
        result[0].Name.ShouldBe("triage");
    }

    [Fact]
    public async Task GetUnitSeedAsync_ByUserFacingUnitId_DoesNotMatch()
    {
        var (provider, scopeFactory) = BuildProvider();
        var definition = JsonSerializer.SerializeToElement(new
        {
            expertise = new[] { new { domain = "triage", level = "expert" } },
        });
        await SeedUnitAsync(
            scopeFactory, unitId: "triage", actorId: "actor-triage", definition: definition);

        var result = await provider.GetUnitSeedAsync(
            "triage", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetUnitSeedAsync_GuidCollisionOnAnotherUnitsUnitId_DoesNotMisMatch()
    {
        var (provider, scopeFactory) = BuildProvider();
        var collidingId = Guid.NewGuid().ToString();

        await SeedUnitAsync(
            scopeFactory,
            unitId: "unit-a",
            actorId: collidingId,
            definition: JsonSerializer.SerializeToElement(new
            {
                expertise = new[] { new { domain = "a-domain", level = "expert" } },
            }));

        await SeedUnitAsync(
            scopeFactory,
            unitId: collidingId,
            actorId: "actor-b",
            definition: JsonSerializer.SerializeToElement(new
            {
                expertise = new[] { new { domain = "b-domain", level = "novice" } },
            }));

        var result = await provider.GetUnitSeedAsync(
            collidingId, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Count.ShouldBe(1);
        result[0].Name.ShouldBe("a-domain");
    }

    [Fact]
    public async Task GetUnitSeedAsync_SoftDeleted_ReturnsNull()
    {
        var (provider, scopeFactory) = BuildProvider();
        var definition = JsonSerializer.SerializeToElement(new
        {
            expertise = new[] { new { domain = "triage", level = "expert" } },
        });
        await SeedUnitAsync(
            scopeFactory,
            unitId: "triage",
            actorId: "actor-triage-deleted",
            definition: definition,
            deletedAt: DateTimeOffset.UtcNow);

        var result = await provider.GetUnitSeedAsync(
            "actor-triage-deleted", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    private static (DbExpertiseSeedProvider Provider, IServiceScopeFactory ScopeFactory) BuildProvider()
    {
        // Capture the DB name in a local so the options builder callback —
        // which fires once per scope when options is Scoped — resolves to the
        // same name every time. Interpolating Guid.NewGuid() inside the
        // callback silently gave every scope its own empty in-memory database.
        var dbName = $"expertise-seed-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<SpringDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var provider = new DbExpertiseSeedProvider(scopeFactory, NullLoggerFactory.Instance);
        return (provider, scopeFactory);
    }

    private static async Task SeedAgentAsync(
        IServiceScopeFactory scopeFactory,
        string agentId,
        string actorId,
        JsonElement? definition,
        DateTimeOffset? deletedAt = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        JsonElement? stableDefinition = null;
        if (definition is { } el)
        {
            using var doc = JsonDocument.Parse(el.GetRawText());
            stableDefinition = doc.RootElement.Clone();
        }

        db.AgentDefinitions.Add(new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            ActorId = actorId,
            Name = agentId,
            Description = "test",
            Definition = stableDefinition,
            DeletedAt = deletedAt,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task SeedUnitAsync(
        IServiceScopeFactory scopeFactory,
        string unitId,
        string actorId,
        JsonElement? definition,
        DateTimeOffset? deletedAt = null)
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
            UnitId = unitId,
            ActorId = actorId,
            Name = unitId,
            Description = "test",
            Definition = stableDefinition,
            DeletedAt = deletedAt,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}