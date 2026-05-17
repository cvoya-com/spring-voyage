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
    private readonly AgentInstructionsBuilder _agentInstructionsBuilder = new();
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
            _agentInstructionsBuilder,
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
            Policies: null,
            Skills: [new Skill("deploy", "Deploys services", [
                new ToolDefinition("ops.run_deploy", "Runs deployment", JsonSerializer.SerializeToElement(new { }))
            ])],
            PriorMessages: [],
            LastCheckpoint: null,
            AgentInstructions: null);

        var result = await _assembler.AssembleAsync(message, context, TestContext.Current.CancellationToken);

        result.ShouldContain("## Unit Context");
        result.ShouldContain("deploy");
        result.ShouldContain("Deploys services");
        result.ShouldContain("ops.run_deploy");
    }

    /// <summary>
    /// #2360: unit-equipped skill bundles render in Layer 2; agent-equipped
    /// bundles render in Layer 4. The same SkillBundle going through the
    /// agent slot must land under "## Agent Instructions", not "## Unit
    /// Context", so member-agent inheritance (unit → Layer 2) does not
    /// conflate with the agent's own bundles (Layer 4).
    /// </summary>
    [Fact]
    public async Task AssembleAsync_UnitBundles_RenderInLayer2()
    {
        var message = CreateMessage();
        var bundle = new SkillBundle(
            PackageName: "spring-voyage/software-engineering",
            SkillName: "triage-and-assign",
            Prompt: "## Triage prompt body",
            RequiredTools: Array.Empty<SkillToolRequirement>());

        var context = new PromptAssemblyContext(
            Policies: null,
            Skills: null,
            PriorMessages: [],
            LastCheckpoint: null,
            AgentInstructions: null,
            SkillBundles: new[] { bundle });

        var result = await _assembler.AssembleAsync(message, context, TestContext.Current.CancellationToken);

        result.ShouldContain("## Unit Context");
        var unitIdx = result.IndexOf("## Unit Context", StringComparison.Ordinal);
        var bundleIdx = result.IndexOf("triage-and-assign", StringComparison.Ordinal);
        bundleIdx.ShouldBeGreaterThan(unitIdx);
        result.ShouldNotContain("## Agent Instructions");
    }

    [Fact]
    public async Task AssembleAsync_AgentBundles_RenderInLayer4()
    {
        var message = CreateMessage();
        var bundle = new SkillBundle(
            PackageName: "spring-voyage/software-engineering",
            SkillName: "pr-review-cycle",
            Prompt: "## PR Review prompt body",
            RequiredTools: Array.Empty<SkillToolRequirement>());

        var context = new PromptAssemblyContext(
            Policies: null,
            Skills: null,
            PriorMessages: [],
            LastCheckpoint: null,
            AgentInstructions: null,
            AgentSkillBundles: new[] { bundle });

        var result = await _assembler.AssembleAsync(message, context, TestContext.Current.CancellationToken);

        result.ShouldContain("## Agent Instructions");
        var layer4Idx = result.IndexOf("## Agent Instructions", StringComparison.Ordinal);
        var bundleIdx = result.IndexOf("pr-review-cycle", StringComparison.Ordinal);
        bundleIdx.ShouldBeGreaterThan(layer4Idx);

        // Must not leak the bundle body into Layer 2 — the two paths are
        // strictly separated even when both feature only the agent
        // bundles slot.
        result.ShouldNotContain("## Unit Context");
    }

    /// <summary>
    /// #2442: the assembler renders an auto-injected "Connector context"
    /// section between the platform layer and the unit-context layer
    /// when the per-invocation context carries connector prompt
    /// fragments. The section header is the canonical, stable text the
    /// resolver and docs both pin against.
    /// </summary>
    [Fact]
    public async Task AssembleAsync_ConnectorPromptFragments_RenderUnderPlatformSection()
    {
        var message = CreateMessage();
        var context = new PromptAssemblyContext(
            Policies: null,
            Skills: null,
            PriorMessages: [],
            LastCheckpoint: null,
            AgentInstructions: "agent body",
            ConnectorPromptFragments: new[]
            {
                "### GitHub binding — cvoya-com/spring-voyage\nbody-a",
                "### Other binding\nbody-b",
            });

        var result = await _assembler.AssembleAsync(message, context, TestContext.Current.CancellationToken);

        result.ShouldContain("## Connector context (auto-injected by platform)");
        result.ShouldContain("### GitHub binding — cvoya-com/spring-voyage");
        result.ShouldContain("### Other binding");
        result.ShouldContain("body-a");
        result.ShouldContain("body-b");

        var platformIdx = result.IndexOf("## Platform Instructions", StringComparison.Ordinal);
        var connectorIdx = result.IndexOf("## Connector context", StringComparison.Ordinal);
        var agentIdx = result.IndexOf("## Agent Instructions", StringComparison.Ordinal);
        platformIdx.ShouldBeLessThan(connectorIdx);
        connectorIdx.ShouldBeLessThan(agentIdx);
    }

    [Fact]
    public async Task AssembleAsync_NoConnectorPromptFragments_OmitsConnectorSectionEntirely()
    {
        var message = CreateMessage();
        var context = new PromptAssemblyContext(
            Policies: null,
            Skills: null,
            PriorMessages: [],
            LastCheckpoint: null,
            AgentInstructions: "agent body");

        var result = await _assembler.AssembleAsync(message, context, TestContext.Current.CancellationToken);

        result.ShouldNotContain("## Connector context");
    }

    [Fact]
    public async Task AssembleAsync_NullAndBlankConnectorFragments_AreSkipped()
    {
        var message = CreateMessage();
        var context = new PromptAssemblyContext(
            Policies: null,
            Skills: null,
            PriorMessages: [],
            LastCheckpoint: null,
            AgentInstructions: null,
            ConnectorPromptFragments: new[]
            {
                "### Real fragment\nbody",
                string.Empty,
                "   ",
            });

        var result = await _assembler.AssembleAsync(message, context, TestContext.Current.CancellationToken);

        result.ShouldContain("### Real fragment");
        // The whitespace-only entries do not produce any extra heading-
        // looking output; the section heading appears exactly once.
        var firstSection = result.IndexOf("## Connector context", StringComparison.Ordinal);
        var secondSection = result.IndexOf("## Connector context", firstSection + 1, StringComparison.Ordinal);
        secondSection.ShouldBe(-1);
    }

    [Fact]
    public async Task AssembleAsync_UnitAndAgentBundles_RenderInDistinctLayers()
    {
        var message = CreateMessage();
        var unitBundle = new SkillBundle(
            PackageName: "pkg-u",
            SkillName: "skill-u",
            Prompt: "unit-bundle-body",
            RequiredTools: Array.Empty<SkillToolRequirement>());
        var agentBundle = new SkillBundle(
            PackageName: "pkg-a",
            SkillName: "skill-a",
            Prompt: "agent-bundle-body",
            RequiredTools: Array.Empty<SkillToolRequirement>());

        var context = new PromptAssemblyContext(
            Policies: null,
            Skills: null,
            PriorMessages: [],
            LastCheckpoint: null,
            AgentInstructions: "user-instructions",
            SkillBundles: new[] { unitBundle },
            AgentSkillBundles: new[] { agentBundle });

        var result = await _assembler.AssembleAsync(message, context, TestContext.Current.CancellationToken);

        var unitCtxIdx = result.IndexOf("## Unit Context", StringComparison.Ordinal);
        var agentCtxIdx = result.IndexOf("## Agent Instructions", StringComparison.Ordinal);
        var unitBundleIdx = result.IndexOf("skill-u", StringComparison.Ordinal);
        var agentBundleIdx = result.IndexOf("skill-a", StringComparison.Ordinal);
        var instructionsIdx = result.IndexOf("user-instructions", StringComparison.Ordinal);

        unitCtxIdx.ShouldBeGreaterThanOrEqualTo(0);
        agentCtxIdx.ShouldBeGreaterThan(unitCtxIdx);

        // Unit bundle renders inside Layer 2; agent bundle + user
        // instructions render inside Layer 4 — and the instructions
        // appear before the bundle subsection within Layer 4.
        unitBundleIdx.ShouldBeInRange(unitCtxIdx, agentCtxIdx);
        instructionsIdx.ShouldBeGreaterThan(agentCtxIdx);
        agentBundleIdx.ShouldBeGreaterThan(instructionsIdx);
    }
}
