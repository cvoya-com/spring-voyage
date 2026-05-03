// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Security;
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
/// End-to-end-ish test for the <c>UnitCreationService</c> → EF persistence
/// path introduced by #488. Verifies that when a manifest carries an
/// <c>expertise:</c> block, the service writes it onto
/// <see cref="UnitDefinitionEntity.Definition"/> so the unit actor's
/// <c>OnActivateAsync</c> seed provider can pick it up.
/// </summary>
public class UnitCreationServiceExpertiseSeedTests
{
    [Fact]
    public async Task CreateFromManifestAsync_WithExpertise_PersistsOntoDefinition()
    {
        var dbName = $"seed-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<SpringDbContext>(opt => opt.UseInMemoryDatabase(dbName));

        var mockIdentityResolver = Substitute.For<IHumanIdentityResolver>();
        mockIdentityResolver.ResolveByUsernameAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        services.AddSingleton<IHumanIdentityResolver>(mockIdentityResolver);

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        // Pre-register a UnitDefinitionEntity as if DirectoryService.RegisterAsync
        // had run — in production the directory upserts this row as part of
        // the unit's creation; the in-memory provider is stubbed here so we
        // don't need the full routing stack.
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = Guid.NewGuid(),
                DisplayName = "research-cell",
                Description = "test",
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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

        var manifest = new UnitManifest
        {
            DisplayName = "research-cell",
            Description = "a research cell",
            Expertise = new List<ExpertiseManifestEntry>
            {
                new() { Domain = "llm-eval", Level = "expert" },
                new() { Domain = "dataset-curation", Level = "advanced", Description = "building eval sets" },
            },
        };

        await service.CreateFromManifestAsync(manifest, new UnitCreationOverrides(IsTopLevel: true), TestContext.Current.CancellationToken);

        // Re-open the DB and verify the Definition JSON carries the expertise.
        using var verifyScope = scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var persisted = await verifyDb.UnitDefinitions.FirstAsync(
            u => u.UnitId == "research-cell",
            TestContext.Current.CancellationToken);
        persisted.Definition.ShouldNotBeNull();

        var json = persisted.Definition!.Value;
        json.TryGetProperty("expertise", out var expertise).ShouldBeTrue();
        expertise.ValueKind.ShouldBe(JsonValueKind.Array);
        expertise.GetArrayLength().ShouldBe(2);
        expertise[0].GetProperty("domain").GetString().ShouldBe("llm-eval");
        expertise[0].GetProperty("level").GetString().ShouldBe("expert");
        expertise[1].GetProperty("domain").GetString().ShouldBe("dataset-curation");
        expertise[1].GetProperty("description").GetString().ShouldBe("building eval sets");
    }
}