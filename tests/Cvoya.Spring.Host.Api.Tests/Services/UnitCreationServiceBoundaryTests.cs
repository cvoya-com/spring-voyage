// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Services;
using Cvoya.Spring.Manifest;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies that a manifest carrying a <c>boundary:</c> block is persisted
/// through <see cref="IUnitBoundaryStore"/> during creation — the same path
/// the <c>PUT /api/v1/units/{id}/boundary</c> endpoint uses (#494 /
/// PR-PLAT-BOUND-2b). The round-trip property we care about: a YAML-declared
/// boundary ends up wire-identical to an API-set one, so downstream
/// filtering / projection / synthesis behave the same.
/// </summary>
public class UnitCreationServiceBoundaryTests
{
    [Fact]
    public async Task CreateFromManifestAsync_WithBoundary_WritesThroughIUnitBoundaryStore()
    {
        var (service, boundaryStore) = BuildService("boundary-cell");

        var manifest = new UnitManifest
        {
            Name = "boundary-cell",
            Description = "cell with a declared boundary",
            Boundary = new BoundaryManifest
            {
                Opacities = new()
                {
                    new BoundaryOpacityManifestEntry { DomainPattern = "internal-*" },
                    new BoundaryOpacityManifestEntry { OriginPattern = "agent://secret-*" },
                },
                Projections = new()
                {
                    new BoundaryProjectionManifestEntry
                    {
                        DomainPattern = "backend-*",
                        RenameTo = "engineering",
                        OverrideLevel = "advanced",
                    },
                },
                Syntheses = new()
                {
                    new BoundarySynthesisManifestEntry
                    {
                        Name = "full-stack",
                        Level = "expert",
                    },
                },
            },
        };

        await service.CreateFromManifestAsync(
            manifest, new UnitCreationOverrides(), TestContext.Current.CancellationToken);

        // One SetAsync call carrying the projected UnitBoundary — check the
        // exact content so YAML → core projection stays stable.
        await boundaryStore.Received(1).SetAsync(
            Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == "boundary-cell"),
            Arg.Is<UnitBoundary>(b =>
                b.Opacities != null && b.Opacities.Count == 2
                && b.Projections != null && b.Projections.Count == 1
                && b.Syntheses != null && b.Syntheses.Count == 1
                && b.Projections[0].RenameTo == "engineering"
                && b.Projections[0].OverrideLevel == ExpertiseLevel.Advanced
                && b.Syntheses[0].Name == "full-stack"
                && b.Syntheses[0].Level == ExpertiseLevel.Expert),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateFromManifestAsync_NoBoundaryBlock_DoesNotCallBoundaryStore()
    {
        var (service, boundaryStore) = BuildService("plain-cell");

        var manifest = new UnitManifest
        {
            Name = "plain-cell",
            Description = "no boundary declared",
        };

        await service.CreateFromManifestAsync(
            manifest, new UnitCreationOverrides(), TestContext.Current.CancellationToken);

        await boundaryStore.DidNotReceive().SetAsync(
            Arg.Any<Address>(),
            Arg.Any<UnitBoundary>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateFromManifestAsync_EmptyBoundaryBlock_DoesNotCallBoundaryStore()
    {
        var (service, boundaryStore) = BuildService("empty-boundary");

        var manifest = new UnitManifest
        {
            Name = "empty-boundary",
            Description = "boundary block present but empty",
            Boundary = new BoundaryManifest
            {
                Opacities = new List<BoundaryOpacityManifestEntry>(),
                Projections = new List<BoundaryProjectionManifestEntry>(),
                Syntheses = new List<BoundarySynthesisManifestEntry>(),
            },
        };

        await service.CreateFromManifestAsync(
            manifest, new UnitCreationOverrides(), TestContext.Current.CancellationToken);

        await boundaryStore.DidNotReceive().SetAsync(
            Arg.Any<Address>(),
            Arg.Any<UnitBoundary>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateFromManifestAsync_BoundaryStoreThrows_NonFatal()
    {
        // Boundary write failures are non-fatal — the unit is already live;
        // log the warning and move on so a transient actor hiccup doesn't
        // abort creation. The operator can push the boundary via PUT later.
        var (service, boundaryStore) = BuildService("flaky-boundary");
        boundaryStore
            .SetAsync(Arg.Any<Address>(), Arg.Any<UnitBoundary>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("actor unreachable")));

        var manifest = new UnitManifest
        {
            Name = "flaky-boundary",
            Description = "store throws on write",
            Boundary = new BoundaryManifest
            {
                Opacities = new()
                {
                    new BoundaryOpacityManifestEntry { DomainPattern = "hidden-*" },
                },
            },
        };

        // Should not throw.
        var result = await service.CreateFromManifestAsync(
            manifest, new UnitCreationOverrides(), TestContext.Current.CancellationToken);

        result.Unit.Name.ShouldBe("flaky-boundary");
    }

    [Fact]
    public async Task CreateFromManifestAsync_SynthesisWithBlankName_Dropped()
    {
        // Synthesis entries with a blank name are dropped at the mapper
        // layer so a misspelled manifest never fabricates an empty
        // team capability. If every synthesis entry is blank and no other
        // rule survives, the store write is skipped entirely.
        var (service, boundaryStore) = BuildService("blank-synthesis");

        var manifest = new UnitManifest
        {
            Name = "blank-synthesis",
            Description = "only blank syntheses",
            Boundary = new BoundaryManifest
            {
                Syntheses = new()
                {
                    new BoundarySynthesisManifestEntry { Name = "  " },
                    new BoundarySynthesisManifestEntry { Name = null },
                },
            },
        };

        await service.CreateFromManifestAsync(
            manifest, new UnitCreationOverrides(), TestContext.Current.CancellationToken);

        await boundaryStore.DidNotReceive().SetAsync(
            Arg.Any<Address>(),
            Arg.Any<UnitBoundary>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateFromManifestAsync_UnknownLevelString_ResolvesToNull()
    {
        // Unknown override_level / level strings resolve to null rather
        // than failing deserialisation — matches the HTTP DTO tolerance so
        // a bad level doesn't poison an entire manifest apply.
        var (service, boundaryStore) = BuildService("tolerant-level");

        var manifest = new UnitManifest
        {
            Name = "tolerant-level",
            Description = "unknown levels resolve to null",
            Boundary = new BoundaryManifest
            {
                Projections = new()
                {
                    new BoundaryProjectionManifestEntry
                    {
                        DomainPattern = "x",
                        OverrideLevel = "not-a-level",
                    },
                },
            },
        };

        await service.CreateFromManifestAsync(
            manifest, new UnitCreationOverrides(), TestContext.Current.CancellationToken);

        await boundaryStore.Received(1).SetAsync(
            Arg.Any<Address>(),
            Arg.Is<UnitBoundary>(b =>
                b.Projections != null
                && b.Projections.Count == 1
                && b.Projections[0].OverrideLevel == null),
            Arg.Any<CancellationToken>());
    }

    private static (UnitCreationService Service, IUnitBoundaryStore BoundaryStore) BuildService(string unitId)
    {
        var dbName = $"boundary-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<SpringDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        // Pre-seed the UnitDefinitionEntity row so the expertise / orch
        // persist paths — which run before the boundary write in the happy
        // path — find an existing row. Boundary persistence itself doesn't
        // need the row but the surrounding pipeline does.
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = Guid.NewGuid(),
                UnitId = unitId,
                ActorId = Guid.NewGuid().ToString(),
                Name = unitId,
                Description = "test",
            });
            db.SaveChanges();
        }

        var directory = Substitute.For<IDirectoryService>();
        directory.RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var unitProxy = Substitute.For<IUnitActor>();
        unitProxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        var actorProxyFactory = Substitute.For<IActorProxyFactory>();
        actorProxyFactory.CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>()).Returns(unitProxy);
        actorProxyFactory.CreateActorProxy<IHumanActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(Substitute.For<IHumanActor>());

        var boundaryStore = Substitute.For<IUnitBoundaryStore>();

        var service = new UnitCreationService(
            directory,
            actorProxyFactory,
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<IUnitConnectorConfigStore>(),
            Array.Empty<IConnectorType>(),
            Substitute.For<ISkillBundleResolver>(),
            Substitute.For<ISkillBundleValidator>(),
            Substitute.For<IUnitSkillBundleStore>(),
            Substitute.For<IUnitMembershipRepository>(),
            scopeFactory,
            NullLoggerFactory.Instance,
            boundaryStore);

        return (service, boundaryStore);
    }
}