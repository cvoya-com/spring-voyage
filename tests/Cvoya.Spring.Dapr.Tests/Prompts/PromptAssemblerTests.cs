// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Prompts;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Prompts;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="PromptAssembler"/>.
/// </summary>
public class PromptAssemblerTests
{
    private readonly IPlatformPromptProvider _platformProvider = Substitute.For<IPlatformPromptProvider>();
    private readonly UnitContextBuilder _unitContextBuilder = new();
    private readonly ThreadContextBuilder _threadContextBuilder = new();
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
            _threadContextBuilder,
            _loggerFactory);
    }

    private static Message CreateMessage(string text = "hello")
    {
        return new Message(
            Guid.NewGuid(),
            Address.For("agent", TestSlugIds.HexFor("sender")),
            Address.For("agent", TestSlugIds.HexFor("receiver")),
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
        var context = new PromptAssemblyContext(
            Members: [Address.For("agent", TestSlugIds.HexFor("team/alice"))],
            Policies: JsonSerializer.SerializeToElement(new { maxRetries = 3 }),
            Skills: [new Skill("review", "Code review", [])],
            PriorMessages: [CreateMessage("prior msg")],
            LastCheckpoint: "checkpoint-1",
            AgentInstructions: "You are a code reviewer.");

        var result = await _assembler.AssembleAsync(message, context, TestContext.Current.CancellationToken);

        result.ShouldContain("## Platform Instructions");
        result.ShouldContain("## Unit Context");
        result.ShouldContain("## Thread Context");
        result.ShouldContain("## Agent Instructions");

        // Verify ordering
        var platformIdx = result.IndexOf("## Platform Instructions", StringComparison.Ordinal);
        var unitIdx = result.IndexOf("## Unit Context", StringComparison.Ordinal);
        var convIdx = result.IndexOf("## Thread Context", StringComparison.Ordinal);
        var agentIdx = result.IndexOf("## Agent Instructions", StringComparison.Ordinal);

        platformIdx.ShouldBeLessThan(unitIdx);
        unitIdx.ShouldBeLessThan(convIdx);
        convIdx.ShouldBeLessThan(agentIdx);
    }

    /// <summary>
    /// Verifies that empty layers are omitted gracefully.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_OmitsEmptyLayersGracefully()
    {
        var message = CreateMessage();
        var context = new PromptAssemblyContext(
            Members: [],
            Policies: null,
            Skills: null,
            PriorMessages: [],
            LastCheckpoint: null,
            AgentInstructions: null);

        var result = await _assembler.AssembleAsync(message, context, TestContext.Current.CancellationToken);

        result.ShouldContain("## Platform Instructions");
        result.ShouldNotContain("## Unit Context");
        result.ShouldNotContain("## Thread Context");
        result.ShouldNotContain("## Agent Instructions");
    }

    /// <summary>
    /// Verifies that calling with no context at all produces just the platform layer.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_NullContext_OnlyPlatformLayer()
    {
        var message = CreateMessage();

        var result = await _assembler.AssembleAsync(message, context: null, TestContext.Current.CancellationToken);

        result.ShouldContain("## Platform Instructions");
        result.ShouldNotContain("## Unit Context");
        result.ShouldNotContain("## Thread Context");
        result.ShouldNotContain("## Agent Instructions");
    }

    /// <summary>
    /// Verifies that skill descriptions are included in the unit context layer.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_IncludesSkillDescriptionsInUnitContext()
    {
        var message = CreateMessage();
        var context = new PromptAssemblyContext(
            Members: [],
            Policies: null,
            Skills: [new Skill("deploy", "Deploys services", [
                new ToolDefinition("run-deploy", "Runs deployment", JsonSerializer.SerializeToElement(new { }))
            ])],
            PriorMessages: [],
            LastCheckpoint: null,
            AgentInstructions: null);

        var result = await _assembler.AssembleAsync(message, context, TestContext.Current.CancellationToken);

        result.ShouldContain("## Unit Context");
        result.ShouldContain("deploy");
        result.ShouldContain("Deploys services");
        result.ShouldContain("run-deploy");
    }
}