// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Execution;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DbAgentDefinitionProvider.Project"/>, which extracts
/// execution config from the persisted JSON definition. The DB integration is
/// exercised indirectly via <see cref="SpringDbContext"/> tests.
/// </summary>
public class DbAgentDefinitionProviderTests
{
    [Fact]
    public void Project_ExtractsTopLevelExecutionBlock()
    {
        var entity = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            AgentId = "ada",
            Name = "Ada",
            Definition = JsonSerializer.SerializeToElement(new
            {
                instructions = "Be careful.",
                execution = new { tool = "claude-code", image = "spring-agent:latest", runtime = "docker" }
            })
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        def.Instructions.ShouldBe("Be careful.");
        def.Execution.ShouldNotBeNull();
        def.Execution!.Tool.ShouldBe("claude-code");
        def.Execution.Image.ShouldBe("spring-agent:latest");
        def.Execution.Runtime.ShouldBe("docker");
    }

    [Fact]
    public void Project_ExtractsLegacyAiEnvironmentBlock()
    {
        var entity = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            AgentId = "ada",
            Name = "Ada",
            Definition = JsonSerializer.SerializeToElement(new
            {
                ai = new
                {
                    tool = "claude-code",
                    environment = new { image = "legacy:v1" }
                }
            })
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        def.Execution.ShouldNotBeNull();
        def.Execution!.Tool.ShouldBe("claude-code");
        def.Execution.Image.ShouldBe("legacy:v1");
    }

    [Fact]
    public void Project_MissingExecution_ReturnsNullExecution()
    {
        var entity = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            AgentId = "ada",
            Name = "Ada",
            Definition = JsonSerializer.SerializeToElement(new { instructions = "do things" })
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        def.Execution.ShouldBeNull();
        def.Instructions.ShouldBe("do things");
    }

    [Fact]
    public void Project_NullDefinition_ReturnsEmptyDefinition()
    {
        var entity = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            AgentId = "ada",
            Name = "Ada",
            Definition = null
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        def.AgentId.ShouldBe("ada");
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
            AgentId = "ada",
            Name = "Ada",
            Definition = JsonSerializer.SerializeToElement(new
            {
                execution = new { tool = "claude-code", image = "spring-agent:latest", hosting = "persistent" }
            })
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        def.Execution.ShouldNotBeNull();
        def.Execution!.Hosting.ShouldBe(AgentHostingMode.Persistent);
    }

    [Fact]
    public void Project_MissingHosting_DefaultsToEphemeral()
    {
        var entity = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            AgentId = "ada",
            Name = "Ada",
            Definition = JsonSerializer.SerializeToElement(new
            {
                execution = new { tool = "claude-code", image = "spring-agent:latest" }
            })
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        def.Execution.ShouldNotBeNull();
        def.Execution!.Hosting.ShouldBe(AgentHostingMode.Ephemeral);
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
            AgentId = "ada",
            Name = "Ada",
            Definition = JsonSerializer.SerializeToElement(new
            {
                execution = new { tool = "claude-code", image = "spring-agent:latest", hosting = "pooled" }
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
        // fields so DaprAgentLauncher can forward them to the container.
        var entity = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            AgentId = "ada",
            Name = "Ada",
            Definition = JsonSerializer.SerializeToElement(new
            {
                execution = new
                {
                    tool = "dapr-agent",
                    image = "localhost/spring-voyage-agent-dapr:latest",
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
            AgentId = "ada",
            Name = "Ada",
            Definition = JsonSerializer.SerializeToElement(new
            {
                execution = new { tool = "dapr-agent", image = "localhost/spring-voyage-agent-dapr:latest" }
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
            AgentId = "ada",
            Name = "Ada",
            Definition = JsonSerializer.SerializeToElement(new
            {
                execution = new { tool = "custom", hosting = "persistent" }
            })
        };

        var def = DbAgentDefinitionProvider.Project(entity);

        def.Execution.ShouldNotBeNull();
        def.Execution!.Tool.ShouldBe("custom");
        def.Execution.Image.ShouldBeNull();
        def.Execution.Hosting.ShouldBe(AgentHostingMode.Persistent);
    }
}