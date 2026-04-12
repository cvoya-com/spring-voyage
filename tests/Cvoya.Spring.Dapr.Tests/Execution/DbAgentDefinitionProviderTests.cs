// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Text.Json;

using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Execution;

using FluentAssertions;

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

        def.Instructions.Should().Be("Be careful.");
        def.Execution.Should().NotBeNull();
        def.Execution!.Tool.Should().Be("claude-code");
        def.Execution.Image.Should().Be("spring-agent:latest");
        def.Execution.Runtime.Should().Be("docker");
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

        def.Execution.Should().NotBeNull();
        def.Execution!.Tool.Should().Be("claude-code");
        def.Execution.Image.Should().Be("legacy:v1");
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

        def.Execution.Should().BeNull();
        def.Instructions.Should().Be("do things");
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

        def.AgentId.Should().Be("ada");
        def.Name.Should().Be("Ada");
        def.Instructions.Should().BeNull();
        def.Execution.Should().BeNull();
    }
}