// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Prompts;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Prompts;

using FluentAssertions;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Xunit;

/// <summary>
/// Unit tests for <see cref="PromptAssembler"/>.
/// </summary>
public class PromptAssemblerTests
{
    private readonly IPlatformPromptProvider _platformProvider = Substitute.For<IPlatformPromptProvider>();
    private readonly UnitContextBuilder _unitContextBuilder = new();
    private readonly ConversationContextBuilder _conversationContextBuilder = new();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly PromptAssembler _assembler;

    public PromptAssemblerTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _platformProvider.GetPlatformPromptAsync(Arg.Any<CancellationToken>())
            .Returns("Platform safety rules.");
        _assembler = new PromptAssembler(
            _platformProvider,
            _unitContextBuilder,
            _conversationContextBuilder,
            _loggerFactory);
    }

    private static Message CreateMessage(string text = "hello")
    {
        return new Message(
            Guid.NewGuid(),
            new Address("agent", "sender"),
            new Address("agent", "receiver"),
            MessageType.Domain,
            "conv-1",
            JsonSerializer.SerializeToElement(new { text }),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Verifies that all four layers are included in order when context is set.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_IncludesAllFourLayersInOrder()
    {
        var message = CreateMessage();
        _assembler.Context = new PromptAssemblyContext(
            Members: [new Address("agent", "team/alice")],
            Policies: JsonSerializer.SerializeToElement(new { maxRetries = 3 }),
            Skills: [new Skill("review", "Code review", [])],
            PriorMessages: [CreateMessage("prior msg")],
            LastCheckpoint: "checkpoint-1",
            AgentInstructions: "You are a code reviewer.",
            Mode: ExecutionMode.Hosted);

        var result = await _assembler.AssembleAsync(message, TestContext.Current.CancellationToken);

        result.Should().Contain("## Platform Instructions");
        result.Should().Contain("## Unit Context");
        result.Should().Contain("## Conversation Context");
        result.Should().Contain("## Agent Instructions");

        // Verify ordering
        var platformIdx = result.IndexOf("## Platform Instructions", StringComparison.Ordinal);
        var unitIdx = result.IndexOf("## Unit Context", StringComparison.Ordinal);
        var convIdx = result.IndexOf("## Conversation Context", StringComparison.Ordinal);
        var agentIdx = result.IndexOf("## Agent Instructions", StringComparison.Ordinal);

        platformIdx.Should().BeLessThan(unitIdx);
        unitIdx.Should().BeLessThan(convIdx);
        convIdx.Should().BeLessThan(agentIdx);
    }

    /// <summary>
    /// Verifies that empty layers are omitted gracefully.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_OmitsEmptyLayersGracefully()
    {
        var message = CreateMessage();
        _assembler.Context = new PromptAssemblyContext(
            Members: [],
            Policies: null,
            Skills: null,
            PriorMessages: [],
            LastCheckpoint: null,
            AgentInstructions: null,
            Mode: ExecutionMode.Hosted);

        var result = await _assembler.AssembleAsync(message, TestContext.Current.CancellationToken);

        result.Should().Contain("## Platform Instructions");
        result.Should().NotContain("## Unit Context");
        result.Should().NotContain("## Conversation Context");
        result.Should().NotContain("## Agent Instructions");
    }

    /// <summary>
    /// Verifies that <see cref="PromptAssembler.AssembleForToolsAsync"/> surfaces the tools
    /// from <see cref="PromptAssemblyContext.GetAllTools"/> and builds a user turn from the
    /// message payload text.
    /// </summary>
    [Fact]
    public async Task AssembleForToolsAsync_FlowsSkillToolsAndSeedsUserTurn()
    {
        var message = CreateMessage("please help");
        var tool = new ToolDefinition(
            "github_read_file",
            "Reads a file",
            JsonSerializer.SerializeToElement(new { type = "object" }));
        _assembler.Context = new PromptAssemblyContext(
            Members: [],
            Policies: null,
            Skills: [new Skill("github", "GitHub tools", [tool])],
            PriorMessages: [],
            LastCheckpoint: null,
            AgentInstructions: null,
            Mode: ExecutionMode.Hosted);

        var result = await _assembler.AssembleForToolsAsync(message, TestContext.Current.CancellationToken);

        result.SystemPrompt.Should().Contain("## Platform Instructions");
        result.Tools.Should().HaveCount(1);
        result.Tools[0].Name.Should().Be("github_read_file");
        result.InitialTurns.Should().HaveCount(1);
        result.InitialTurns[0].Role.Should().Be("user");
        result.InitialTurns[0].Content.Should().HaveCount(1);
        result.InitialTurns[0].Content[0].Should().BeOfType<ContentBlock.TextBlock>()
            .Which.Text.Should().Be("please help");
    }

    /// <summary>
    /// Verifies that <see cref="PromptAssembler.AssembleForToolsAsync"/> returns an empty
    /// tool list when no context is set.
    /// </summary>
    [Fact]
    public async Task AssembleForToolsAsync_NoContext_ReturnsEmptyTools()
    {
        var message = CreateMessage("hi");

        var result = await _assembler.AssembleForToolsAsync(message, TestContext.Current.CancellationToken);

        result.Tools.Should().BeEmpty();
        result.InitialTurns.Should().HaveCount(1);
    }

    /// <summary>
    /// Verifies that <see cref="PromptAssemblyContext.GetAllTools"/> flattens tools across
    /// multiple skills.
    /// </summary>
    [Fact]
    public void GetAllTools_FlattensAcrossSkills()
    {
        var context = new PromptAssemblyContext(
            Members: [],
            Policies: null,
            Skills:
            [
                new Skill("a", "first", [new ToolDefinition("t1", "", JsonSerializer.SerializeToElement(new { }))]),
                new Skill("b", "second",
                [
                    new ToolDefinition("t2", "", JsonSerializer.SerializeToElement(new { })),
                    new ToolDefinition("t3", "", JsonSerializer.SerializeToElement(new { })),
                ]),
            ],
            PriorMessages: [],
            LastCheckpoint: null,
            AgentInstructions: null,
            Mode: ExecutionMode.Hosted);

        var tools = context.GetAllTools();

        tools.Select(t => t.Name).Should().BeEquivalentTo(["t1", "t2", "t3"]);
    }

    /// <summary>
    /// Verifies that skill descriptions are included in the unit context layer.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_IncludesSkillDescriptionsInUnitContext()
    {
        var message = CreateMessage();
        _assembler.Context = new PromptAssemblyContext(
            Members: [],
            Policies: null,
            Skills: [new Skill("deploy", "Deploys services", [
                new ToolDefinition("run-deploy", "Runs deployment", JsonSerializer.SerializeToElement(new { }))
            ])],
            PriorMessages: [],
            LastCheckpoint: null,
            AgentInstructions: null,
            Mode: ExecutionMode.Hosted);

        var result = await _assembler.AssembleAsync(message, TestContext.Current.CancellationToken);

        result.Should().Contain("## Unit Context");
        result.Should().Contain("deploy");
        result.Should().Contain("Deploys services");
        result.Should().Contain("run-deploy");
    }
}