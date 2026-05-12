// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DbAgentDefinitionProvider.Project"/>, which extracts
/// execution config from the persisted JSON definition. The DB integration is
/// exercised indirectly via <see cref="SpringDbContext"/> tests.
/// </summary>
/// <remarks>
/// #1732: <c>execution.tool</c> on the persisted JSON is silently ignored —
/// the runtime registry derives the tool kind from <c>execution.agent</c>
/// (the runtime id) at dispatch. The projection requires <c>agent</c> to be
/// present to produce an <see cref="AgentExecutionConfig"/>.
/// </remarks>
public class DbAgentDefinitionProviderTests
{
    [Fact]
    public async Task GetByIdAsync_UnitDefinitionWithExecution_ProjectsUnitRuntime()
    {
        var unitId = Guid.NewGuid();
        using var services = BuildProvider(db =>
        {
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = unitId,
                TenantId = OssTenantIds.Default,
                DisplayName = "Runtime Unit",
                Definition = JsonSerializer.SerializeToElement(new
                {
                    instructions = "Coordinate the work.",
                    execution = new
                    {
                        agent = "spring-voyage",
                        image = "ghcr.io/cvoya/unit-runtime:latest",
                        hosting = "persistent",
                        provider = "ollama",
                        model = "llama3.2:3b",
                    },
                }),
            });
        });

        var sut = CreateProvider(services);

        var definition = await sut.GetByIdAsync(
            unitId.ToString("N"),
            TestContext.Current.CancellationToken);

        definition.ShouldNotBeNull();
        definition!.AgentId.ShouldBe(unitId.ToString("N"));
        definition.Name.ShouldBe("Runtime Unit");
        definition.Instructions.ShouldBe("Coordinate the work.");
        definition.Execution.ShouldNotBeNull();
        definition.Execution!.AgentRuntimeId.ShouldBe("spring-voyage");
        definition.Execution.Image.ShouldBe("ghcr.io/cvoya/unit-runtime:latest");
        definition.Execution.Hosting.ShouldBe(AgentHostingMode.Persistent);
        definition.Execution.Provider.ShouldBe("ollama");
        definition.Execution.Model.ShouldBe("llama3.2:3b");
    }

    [Fact]
    public async Task GetByIdAsync_UnitDefinitionWithoutExecution_ReturnsDefinitionForVisibleError()
    {
        var unitId = Guid.NewGuid();
        using var services = BuildProvider(db =>
        {
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = unitId,
                TenantId = OssTenantIds.Default,
                DisplayName = "Misconfigured Unit",
                Definition = JsonSerializer.SerializeToElement(new
                {
                    instructions = "Coordinate the work.",
                }),
            });
        });

        var sut = CreateProvider(services);

        var definition = await sut.GetByIdAsync(
            unitId.ToString("N"),
            TestContext.Current.CancellationToken);

        definition.ShouldNotBeNull();
        definition!.AgentId.ShouldBe(unitId.ToString("N"));
        definition.Name.ShouldBe("Misconfigured Unit");
        definition.Instructions.ShouldBe("Coordinate the work.");
        definition.Execution.ShouldBeNull();
    }

    [Fact]
    public async Task GetByIdAsync_AgentAnchorWithoutExecution_FallsBackToUnitDefinition()
    {
        var unitId = Guid.NewGuid();
        using var services = BuildProvider(db =>
        {
            db.AgentDefinitions.Add(new AgentDefinitionEntity
            {
                Id = unitId,
                TenantId = OssTenantIds.Default,
                DisplayName = "Stale anchor",
                Definition = JsonSerializer.SerializeToElement(new
                {
                    instructions = "stale",
                }),
            });
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = unitId,
                TenantId = OssTenantIds.Default,
                DisplayName = "Runtime Unit",
                Definition = JsonSerializer.SerializeToElement(new
                {
                    instructions = "unit instructions",
                    execution = new
                    {
                        agent = "claude-code",
                        image = "ghcr.io/cvoya/unit-runtime:latest",
                    },
                }),
            });
        });

        var sut = CreateProvider(services);

        var definition = await sut.GetByIdAsync(
            unitId.ToString("N"),
            TestContext.Current.CancellationToken);

        definition.ShouldNotBeNull();
        definition!.Name.ShouldBe("Runtime Unit");
        definition.Instructions.ShouldBe("unit instructions");
        definition.Execution.ShouldNotBeNull();
        definition.Execution!.AgentRuntimeId.ShouldBe("claude-code");
    }

    [Fact]
    public void Project_ExtractsTopLevelExecutionBlock()
    {
        var entity = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Ada",
            Definition = JsonSerializer.SerializeToElement(new
            {
                instructions = "Be careful.",
                execution = new { agent = "claude", image = "ghcr.io/cvoya-com/claude-code-base:latest" }
            })
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        def.Instructions.ShouldBe("Be careful.");
        def.Execution.ShouldNotBeNull();
        def.Execution!.AgentRuntimeId.ShouldBe("claude");
        def.Execution.Image.ShouldBe("ghcr.io/cvoya-com/claude-code-base:latest");
    }

    [Fact]
    public void Project_ExtractsLegacyAiEnvironmentBlock()
    {
        var entity = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Ada",
            Definition = JsonSerializer.SerializeToElement(new
            {
                ai = new
                {
                    agent = "claude",
                    environment = new { image = "legacy:v1" }
                }
            })
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        def.Execution.ShouldNotBeNull();
        def.Execution!.AgentRuntimeId.ShouldBe("claude");
        def.Execution.Image.ShouldBe("legacy:v1");
    }

    [Fact]
    public void Project_MissingExecution_ReturnsNullExecution()
    {
        var entity = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Ada",
            Definition = JsonSerializer.SerializeToElement(new { instructions = "do things" })
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        def.Execution.ShouldBeNull();
        def.Instructions.ShouldBe("do things");
    }

    [Fact]
    public void Project_NullDefinition_ReturnsEmptyDefinition()
    {
        var entityId = Guid.NewGuid();
        var entity = new AgentDefinitionEntity
        {
            Id = entityId,
            DisplayName = "Ada",
            Definition = null
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        def.AgentId.ShouldBe(entityId.ToString("N"));
        def.Name.ShouldBe("Ada");
        def.Instructions.ShouldBeNull();
        def.Execution.ShouldBeNull();
    }

    [Fact]
    public void Project_ExtractsHostingField()
    {
        var entity = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Ada",
            Definition = JsonSerializer.SerializeToElement(new
            {
                execution = new { agent = "claude", image = "ghcr.io/cvoya-com/claude-code-base:latest", hosting = "persistent" }
            })
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        def.Execution.ShouldNotBeNull();
        def.Execution!.Hosting.ShouldBe(AgentHostingMode.Persistent);
    }

    [Fact]
    public void Project_MissingHosting_DefaultsToPersistent()
    {
        var entity = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Ada",
            Definition = JsonSerializer.SerializeToElement(new
            {
                execution = new { agent = "claude", image = "ghcr.io/cvoya-com/claude-code-base:latest" }
            })
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        def.Execution.ShouldNotBeNull();
        def.Execution!.Hosting.ShouldBe(AgentHostingMode.Persistent);
    }

    [Fact]
    public void Project_HostingPooled_ParsesEnumValueButDispatcherWillRejectAtRuntime()
    {
        // PR 1 of #1087 reserves Pooled on the enum so YAML written against
        // #362 round-trips through the projection. The dispatcher rejects
        // it at dispatch time with NotSupportedException — see
        // A2AExecutionDispatcherTests.DispatchAsync_PooledHosting_ThrowsNotSupported.
        var entity = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Ada",
            Definition = JsonSerializer.SerializeToElement(new
            {
                execution = new { agent = "claude", image = "ghcr.io/cvoya-com/claude-code-base:latest", hosting = "pooled" }
            })
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        def.Execution.ShouldNotBeNull();
        def.Execution!.Hosting.ShouldBe(AgentHostingMode.Pooled);
    }

    [Fact]
    public void Project_ExtractsProviderAndModel_ForDaprConversationAgents()
    {
        // #480 step 5: switching the Dapr-Conversation-backed runtime's provider
        // / model must be a YAML-only change. The projection extracts both
        // fields so SpringVoyageAgentLauncher can forward them to the container.
        var entity = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Ada",
            Definition = JsonSerializer.SerializeToElement(new
            {
                execution = new
                {
                    agent = "openai",
                    image = "ghcr.io/cvoya-com/spring-voyage-agent:latest",
                    provider = "openai",
                    model = "gpt-4o-mini",
                }
            })
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        def.Execution.ShouldNotBeNull();
        def.Execution!.Provider.ShouldBe("openai");
        def.Execution.Model.ShouldBe("gpt-4o-mini");
    }

    [Fact]
    public void Project_MissingProviderAndModel_LeavesThemNull()
    {
        var entity = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Ada",
            Definition = JsonSerializer.SerializeToElement(new
            {
                execution = new { agent = "openai", image = "ghcr.io/cvoya-com/spring-voyage-agent:latest" }
            })
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        def.Execution.ShouldNotBeNull();
        def.Execution!.Provider.ShouldBeNull();
        def.Execution.Model.ShouldBeNull();
    }

    [Fact]
    public void Project_NullImage_AllowedForA2ANativeAgents()
    {
        var entity = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Ada",
            Definition = JsonSerializer.SerializeToElement(new
            {
                execution = new { agent = "claude", hosting = "persistent" }
            })
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        def.Execution.ShouldNotBeNull();
        def.Execution!.AgentRuntimeId.ShouldBe("claude");
        def.Execution.Image.ShouldBeNull();
        def.Execution.Hosting.ShouldBe(AgentHostingMode.Persistent);
    }

    [Fact]
    public void Project_LegacyToolField_IsIgnored_WhenAgentMissing()
    {
        // #1732: pre-#1732 'execution.tool' values do not back-fill the
        // runtime id; the runtime id must be present explicitly. This is
        // intentional — the runtime id is the durable identity, and a
        // tool kind value cannot reliably round-trip back to a single
        // runtime when multiple runtimes share a tool kind (e.g.
        // openai/google/ollama all share spring-voyage).
        var entity = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Ada",
            Definition = JsonSerializer.SerializeToElement(new
            {
                execution = new { tool = "claude-code", image = "x" }
            })
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        // execution is null because no agent runtime id was present.
        def.Execution.ShouldBeNull();
    }

    private static ServiceProvider BuildProvider(Action<SpringDbContext> seed)
    {
        var dbName = $"agent-definition-provider-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(OssTenantIds.Default));
        services.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase(dbName));

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        seed(db);
        db.SaveChanges();
        return provider;
    }

    private static DbAgentDefinitionProvider CreateProvider(ServiceProvider provider)
        => new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLoggerFactory.Instance);
}
