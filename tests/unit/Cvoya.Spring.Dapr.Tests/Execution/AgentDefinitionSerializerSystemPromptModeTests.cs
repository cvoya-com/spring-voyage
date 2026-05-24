// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Focused tests for the <c>system_prompt_mode</c> slot emitted by
/// <see cref="AgentDefinitionSerializer"/> (#2691 / #2667). The slot must
/// land on the YAML execution block as the lower-case enum literal so the
/// in-container Python SDK round-trips cleanly with the platform.
/// </summary>
public class AgentDefinitionSerializerSystemPromptModeTests
{
    private readonly IRuntimeCatalog _catalog = Substitute.For<IRuntimeCatalog>();
    private readonly AgentDefinitionSerializer _serializer;

    public AgentDefinitionSerializerSystemPromptModeTests()
    {
        _catalog.GetAgentRuntime(Arg.Any<string>()).Returns((AgentRuntime?)null);
        _serializer = new AgentDefinitionSerializer(_catalog);
    }

    [Fact]
    public void Serialize_EmitsReplaceLiteral_WhenAgentDeclaresReplace()
    {
        var definition = new AgentDefinition(
            AgentId: "00000000000000000000000000000001",
            Name: "router",
            Instructions: "you route messages",
            Execution: new AgentExecutionConfig(
                Runtime: "claude-code",
                Image: "ghcr.io/example/claude:latest",
                SystemPromptMode: SystemPromptMode.Replace));

        var yaml = _serializer.SerializeAgentDefinitionYaml(definition);

        yaml.ShouldContain("system_prompt_mode: replace");
    }

    [Fact]
    public void Serialize_EmitsAppendLiteral_WhenAgentDeclaresAppend()
    {
        var definition = new AgentDefinition(
            AgentId: "00000000000000000000000000000001",
            Name: "engineer",
            Instructions: null,
            Execution: new AgentExecutionConfig(
                Runtime: "claude-code",
                Image: "ghcr.io/example/claude:latest",
                SystemPromptMode: SystemPromptMode.Append));

        var yaml = _serializer.SerializeAgentDefinitionYaml(definition);

        yaml.ShouldContain("system_prompt_mode: append");
    }

    [Fact]
    public void Serialize_OmitsSlot_WhenSystemPromptModeIsNull()
    {
        var definition = new AgentDefinition(
            AgentId: "00000000000000000000000000000001",
            Name: "engineer",
            Instructions: null,
            Execution: new AgentExecutionConfig(
                Runtime: "claude-code",
                Image: "ghcr.io/example/claude:latest"));

        var yaml = _serializer.SerializeAgentDefinitionYaml(definition);

        yaml.ShouldNotContain("system_prompt_mode");
    }
}
