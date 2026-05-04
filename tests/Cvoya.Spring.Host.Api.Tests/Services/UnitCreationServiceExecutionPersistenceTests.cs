// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Execution;
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
/// Regression test for #1666 — package-installed units always failed
/// validation because <c>UnitCreationService</c> was passing the unit's
/// user-facing name to <see cref="IUnitExecutionStore.SetAsync"/>, which
/// <see cref="DbUnitExecutionStore"/> requires to be a Guid string. The
/// resulting <see cref="ArgumentException"/> was caught and logged at
/// warning level, so <c>unit_definitions.definition->'execution'</c>
/// stayed <c>NULL</c> for every unit and the validator reported
/// <c>ConfigurationIncomplete: missing image,runtime</c> regardless of
/// what the manifest said.
/// </summary>
public class UnitCreationServiceExecutionPersistenceTests
{
    [Fact]
    public async Task CreateFromManifestAsync_WithExecutionBlock_PersistsExecutionOntoDefinition()
    {
        // Pre-mint the actor Guid so the test can pre-seed the
        // UnitDefinitionEntity row (which the in-memory directory stub
        // doesn't upsert) with the *exact* id the store will look up.
        var actorGuid = Guid.NewGuid();
        var (service, scopeFactory) = BuildService("sv-oss-software-engineering", actorGuid);

        var manifest = new UnitManifest
        {
            Name = "sv-oss-software-engineering",
            Description = "regression #1666 — execution block must persist",
            Execution = new ExecutionManifest
            {
                Image = "ghcr.io/cvoya/sv-oss-software-engineering:latest",
                Runtime = "podman",
                Tool = "claude-code",
                Provider = "anthropic",
                Model = "claude-sonnet-4",
            },
        };

        await service.CreateFromManifestAsync(
            manifest,
            new UnitCreationOverrides(IsTopLevel: true, ActorId: actorGuid),
            TestContext.Current.CancellationToken);

        // Re-open the DB and verify the Definition JSON carries the
        // execution block. Pre-fix this assertion would fail because
        // the store rejected the (non-Guid) name and the warning was
        // swallowed — Definition stayed null and validation reported
        // ConfigurationIncomplete: missing image,runtime.
        using var verifyScope = scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var persisted = await verifyDb.UnitDefinitions.FirstAsync(
            u => u.Id == actorGuid,
            TestContext.Current.CancellationToken);

        persisted.Definition.ShouldNotBeNull();
        var json = persisted.Definition!.Value;
        json.TryGetProperty("execution", out var execution).ShouldBeTrue(
            "execution block was not persisted onto unit_definitions.definition");
        execution.ValueKind.ShouldBe(JsonValueKind.Object);
        execution.GetProperty("image").GetString()
            .ShouldBe("ghcr.io/cvoya/sv-oss-software-engineering:latest");
        execution.GetProperty("runtime").GetString().ShouldBe("podman");
        execution.GetProperty("tool").GetString().ShouldBe("claude-code");
        execution.GetProperty("provider").GetString().ShouldBe("anthropic");
        execution.GetProperty("model").GetString().ShouldBe("claude-sonnet-4");
    }

    private static (UnitCreationService Service, IServiceScopeFactory ScopeFactory) BuildService(
        string displayName, Guid actorGuid)
    {
        var dbName = $"exec-persist-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<SpringDbContext>(opt => opt.UseInMemoryDatabase(dbName));

        var mockIdentityResolver = Substitute.For<IHumanIdentityResolver>();
        mockIdentityResolver
            .ResolveByUsernameAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        services.AddSingleton<IHumanIdentityResolver>(mockIdentityResolver);

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        // The directory stub doesn't upsert the UnitDefinitionEntity row
        // that DbUnitExecutionStore expects, so seed it here keyed by the
        // pre-minted actor Guid. In production this row is upserted by
        // DirectoryService.RegisterAsync as part of unit creation.
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = actorGuid,
                DisplayName = displayName,
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
        actorProxyFactory.CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(unitProxy);
        actorProxyFactory.CreateActorProxy<IHumanActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(Substitute.For<IHumanActor>());

        // Use a real DbUnitExecutionStore so the test exercises the same
        // Guid-parsing contract that production hits — a Substitute would
        // accept a name argument and silently mask the regression.
        var executionStore = new DbUnitExecutionStore(scopeFactory, NullLoggerFactory.Instance);

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
            executionStore: executionStore);

        return (service, scopeFactory);
    }
}