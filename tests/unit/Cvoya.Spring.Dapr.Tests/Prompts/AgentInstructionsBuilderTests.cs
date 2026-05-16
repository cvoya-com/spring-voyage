// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Prompts;

using System.Text.Json;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Prompts;

using Shouldly;

using Xunit;

/// <summary>
/// Unit coverage for <see cref="AgentInstructionsBuilder"/> — the Layer 4
/// composer added in #2360 that renders the agent's user-authored
/// instructions plus the agent-equipped skill bundles. Mirrors the
/// existing <see cref="UnitContextBuilder"/> tests for Layer 2 so the
/// two paths render consistently.
/// </summary>
public class AgentInstructionsBuilderTests
{
    private readonly AgentInstructionsBuilder _builder = new();
    private static readonly JsonElement EmptySchema = JsonSerializer.SerializeToElement(new { });

    [Fact]
    public void Build_NoInputs_ReturnsEmpty()
    {
        _builder.Build(null, null).ShouldBeEmpty();
        _builder.Build(string.Empty, null).ShouldBeEmpty();
        _builder.Build(string.Empty, Array.Empty<SkillBundle>()).ShouldBeEmpty();
    }

    [Fact]
    public void Build_InstructionsOnly_RendersInstructionsOnly()
    {
        var result = _builder.Build("You are a code reviewer.", null);

        result.ShouldContain("You are a code reviewer.");
        result.ShouldNotContain("Skill Bundles");
    }

    [Fact]
    public void Build_BundlesOnly_RendersBundleSection()
    {
        var bundles = new[]
        {
            new SkillBundle(
                PackageName: "spring-voyage/software-engineering",
                SkillName: "triage-and-assign",
                Prompt: "## Triage prompt",
                RequiredTools: Array.Empty<SkillToolRequirement>()),
        };

        var result = _builder.Build(null, bundles);

        result.ShouldContain("### Skill Bundles");
        result.ShouldContain("#### spring-voyage/software-engineering/triage-and-assign");
        result.ShouldContain("## Triage prompt");
        result.ShouldNotContain("Required tools:");
    }

    [Fact]
    public void Build_BundlesWithTools_RendersRequiredToolsSection()
    {
        var bundles = new[]
        {
            new SkillBundle(
                PackageName: "spring-voyage/software-engineering",
                SkillName: "triage-and-assign",
                Prompt: "## Triage prompt",
                RequiredTools: new[]
                {
                    new SkillToolRequirement("platform.assign_to_agent", "assign work", EmptySchema, Optional: false),
                    new SkillToolRequirement("github.read_file", "read code", EmptySchema, Optional: true),
                }),
        };

        var result = _builder.Build(null, bundles);

        result.ShouldContain("Required tools:");
        result.ShouldContain("platform.assign_to_agent: assign work");
        result.ShouldContain("github.read_file (optional): read code");
    }

    [Fact]
    public void Build_InstructionsAndBundles_RendersBothInOrder()
    {
        var bundles = new[]
        {
            new SkillBundle(
                PackageName: "pkg",
                SkillName: "skill",
                Prompt: "bundle-prompt",
                RequiredTools: Array.Empty<SkillToolRequirement>()),
        };

        var result = _builder.Build("user-instructions", bundles);

        var instructionsIdx = result.IndexOf("user-instructions", StringComparison.Ordinal);
        var bundlesIdx = result.IndexOf("### Skill Bundles", StringComparison.Ordinal);
        instructionsIdx.ShouldBeGreaterThanOrEqualTo(0);
        bundlesIdx.ShouldBeGreaterThan(instructionsIdx);
    }

    [Fact]
    public void Build_BundleOrder_IsPreserved()
    {
        var bundles = new[]
        {
            new SkillBundle("pkg-a", "skill-a", "prompt-a", Array.Empty<SkillToolRequirement>()),
            new SkillBundle("pkg-b", "skill-b", "prompt-b", Array.Empty<SkillToolRequirement>()),
        };

        var result = _builder.Build(null, bundles);

        var aIdx = result.IndexOf("skill-a", StringComparison.Ordinal);
        var bIdx = result.IndexOf("skill-b", StringComparison.Ordinal);
        aIdx.ShouldBeGreaterThanOrEqualTo(0);
        bIdx.ShouldBeGreaterThan(aIdx);
    }
}
