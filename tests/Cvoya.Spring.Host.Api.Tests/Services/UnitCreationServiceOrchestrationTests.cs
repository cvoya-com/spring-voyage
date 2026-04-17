// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Models;
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
/// Verifies that a manifest carrying <c>orchestration.strategy</c> is
/// persisted onto <see cref="UnitDefinitionEntity.Definition"/> so the
/// unit actor's <see cref="Cvoya.Spring.Core.Orchestration.IOrchestrationStrategyResolver"/>
/// picks it up at dispatch time (#491).
/// </summary>
public class UnitCreationServiceOrchestrationTests
{
    [Fact]
    public async Task CreateFromManifestAsync_WithOrchestrationStrategy_PersistsOntoDefinition()
    {
        var (service, scopeFactory) = BuildService("triage-cell");

        var manifest = new UnitManifest
        {
            Name = "triage-cell",
            Description = "Label-triage cell",
            Orchestration = new OrchestrationManifest { Strategy = "label-routed" },
        };

        await service.CreateFromManifestAsync(
            manifest, new UnitCreationOverrides(), TestContext.Current.CancellationToken);

        using var verifyScope = scopeFactory.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var persisted = await db.UnitDefinitions.FirstAsync(
            u => u.UnitId == "triage-cell",
            TestContext.Current.CancellationToken);

        persisted.Definition.ShouldNotBeNull();
        var json = persisted.Definition!.Value;
        json.TryGetProperty("orchestration", out var orchestration).ShouldBeTrue();
        orchestration.GetProperty("strategy").GetString().ShouldBe("label-routed");
    }

    [Fact]
    public async Task CreateFromManifestAsync_NoOrchestrationBlock_LeavesDefinitionUntouched()
    {
        var (service, scopeFactory) = BuildService("plain-cell");

        var manifest = new UnitManifest
        {
            Name = "plain-cell",
            Description = "no orchestration directive",
        };

        await service.CreateFromManifestAsync(
            manifest, new UnitCreationOverrides(), TestContext.Current.CancellationToken);

        using var verifyScope = scopeFactory.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var persisted = await db.UnitDefinitions.FirstAsync(
            u => u.UnitId == "plain-cell",
            TestContext.Current.CancellationToken);

        // Definition may still be null (no seed blocks at all) or an empty
        // object — either way, no `orchestration` property should have been
        // written. The resolver falls through to the policy / default path.
        if (persisted.Definition is { ValueKind: JsonValueKind.Object } doc)
        {
            doc.TryGetProperty("orchestration", out _).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task CreateFromManifestAsync_WithBothExpertiseAndOrchestration_PersistsBoth()
    {
        var (service, scopeFactory) = BuildService("rich-cell");

        var manifest = new UnitManifest
        {
            Name = "rich-cell",
            Description = "both slots populated",
            Expertise = new List<ExpertiseManifestEntry>
            {
                new() { Domain = "triage", Level = "advanced" },
            },
            Orchestration = new OrchestrationManifest { Strategy = "label-routed" },
        };

        await service.CreateFromManifestAsync(
            manifest, new UnitCreationOverrides(), TestContext.Current.CancellationToken);

        using var verifyScope = scopeFactory.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var persisted = await db.UnitDefinitions.FirstAsync(
            u => u.UnitId == "rich-cell",
            TestContext.Current.CancellationToken);

        var json = persisted.Definition!.Value;
        json.GetProperty("orchestration").GetProperty("strategy").GetString().ShouldBe("label-routed");
        json.GetProperty("expertise").GetArrayLength().ShouldBe(1);
        json.GetProperty("expertise")[0].GetProperty("domain").GetString().ShouldBe("triage");
    }

    [Fact]
    public async Task CreateFromManifestAsync_BlankStrategyString_IsIgnored()
    {
        var (service, scopeFactory) = BuildService("blank-strategy-cell");

        var manifest = new UnitManifest
        {
            Name = "blank-strategy-cell",
            Description = "blank strategy should be skipped",
            Orchestration = new OrchestrationManifest { Strategy = "   " },
        };

        await service.CreateFromManifestAsync(
            manifest, new UnitCreationOverrides(), TestContext.Current.CancellationToken);

        using var verifyScope = scopeFactory.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var persisted = await db.UnitDefinitions.FirstAsync(
            u => u.UnitId == "blank-strategy-cell",
            TestContext.Current.CancellationToken);

        if (persisted.Definition is { ValueKind: JsonValueKind.Object } doc)
        {
            doc.TryGetProperty("orchestration", out _).ShouldBeFalse();
        }
    }

    private static (UnitCreationService Service, IServiceScopeFactory ScopeFactory) BuildService(string unitId)
    {
        var dbName = $"orch-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<SpringDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        // Pre-seed the UnitDefinitionEntity row since the integration tests
        // do not run through DirectoryService.RegisterAsync — matching the
        // pattern in UnitCreationServiceExpertiseSeedTests.
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
            NullLoggerFactory.Instance);

        return (service, scopeFactory);
    }
}