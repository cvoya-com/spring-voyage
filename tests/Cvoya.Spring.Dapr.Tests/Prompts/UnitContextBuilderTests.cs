// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Prompts;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Prompts;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="UnitContextBuilder"/>.
/// </summary>
public class UnitContextBuilderTests
{
    private readonly UnitContextBuilder _builder = new();

    /// <summary>
    /// Verifies that member addresses are included in the output.
    /// </summary>
    [Fact]
    public void Build_IncludesMemberAddresses()
    {
        var members = new List<Address>
        {
            new("agent", "team/alice"),
            new("agent", "team/bob")
        };

        var result = _builder.Build(members, null, null);

        result.ShouldContain("agent://team/alice");
        result.ShouldContain("agent://team/bob");
        result.ShouldContain("Peer Directory");
    }

    /// <summary>
    /// Verifies that policies are included in the output.
    /// </summary>
    [Fact]
    public void Build_IncludesPolicies()
    {
        var policies = JsonSerializer.SerializeToElement(new { maxRetries = 3, timeout = "30s" });

        var result = _builder.Build([], policies, null);

        result.ShouldContain("Policies");
        result.ShouldContain("maxRetries");
    }

    /// <summary>
    /// Verifies that skill descriptions are included in the output.
    /// </summary>
    [Fact]
    public void Build_IncludesSkillDescriptions()
    {
        var skills = new List<Skill>
        {
            new("code-review", "Reviews pull requests", [
                new ToolDefinition("analyze", "Analyzes code changes", JsonSerializer.SerializeToElement(new { }))
            ])
        };

        var result = _builder.Build([], null, skills);

        result.ShouldContain("Available Skills");
        result.ShouldContain("code-review");
        result.ShouldContain("Reviews pull requests");
        result.ShouldContain("analyze");
    }

    /// <summary>
    /// Verifies that empty inputs produce an empty string.
    /// </summary>
    [Fact]
    public void Build_HandlesEmptyInputs()
    {
        var result = _builder.Build([], null, null);

        result.ShouldBeEmpty();
    }

    /// <summary>
    /// Package-level skill bundles (#167) render after the connector-skills
    /// section so the layer-2 ordering stays peer directory → policies →
    /// available skills → skill bundles. Declaration order is preserved.
    /// </summary>
    [Fact]
    public void Build_IncludesSkillBundlesAfterSkills_InDeclarationOrder()
    {
        var emptySchema = JsonSerializer.SerializeToElement(new { });
        var bundles = new List<SkillBundle>
        {
            new("spring-voyage/software-engineering", "triage-and-assign",
                "## Triage & Assignment\nClassify incoming work.",
                new[] { new SkillToolRequirement("assignToAgent", "assign work", emptySchema, false) }),
            new("spring-voyage/software-engineering", "pr-review-cycle",
                "## PR Review Cycle\nRoute PR reviews.",
                Array.Empty<SkillToolRequirement>()),
        };

        var result = _builder.Build([], null, null, bundles);

        result.ShouldContain("### Skill Bundles");
        result.ShouldContain("spring-voyage/software-engineering/triage-and-assign");
        result.ShouldContain("assignToAgent");
        result.ShouldContain("spring-voyage/software-engineering/pr-review-cycle");

        var triageIdx = result.IndexOf("triage-and-assign", StringComparison.Ordinal);
        var reviewIdx = result.IndexOf("pr-review-cycle", StringComparison.Ordinal);
        triageIdx.ShouldBeLessThan(reviewIdx);
    }

    /// <summary>
    /// A bundle with no required tools is prompt-only — it still renders its
    /// prompt fragment but omits the "Required tools" sub-section.
    /// </summary>
    [Fact]
    public void Build_PromptOnlyBundle_OmitsRequiredToolsSubsection()
    {
        var bundles = new List<SkillBundle>
        {
            new("acme/prompt-only", "intro", "## Intro prompt", Array.Empty<SkillToolRequirement>()),
        };

        var result = _builder.Build([], null, null, bundles);

        result.ShouldContain("## Intro prompt");
        result.ShouldNotContain("Required tools:");
    }

    /// <summary>
    /// When both connector-level skills and bundle prompts are present the
    /// connector skills appear first, matching Layer 2 composition.
    /// </summary>
    [Fact]
    public void Build_SkillsAndBundles_SkillsRenderFirst()
    {
        var emptySchema = JsonSerializer.SerializeToElement(new { });
        var skills = new List<Skill>
        {
            new("github", "Tools from GitHub",
                new[] { new ToolDefinition("github_list_issues", "list issues", emptySchema) }),
        };
        var bundles = new List<SkillBundle>
        {
            new("spring-voyage/software-engineering", "triage-and-assign",
                "## Triage prompt", Array.Empty<SkillToolRequirement>()),
        };

        var result = _builder.Build([], null, skills, bundles);

        var skillsIdx = result.IndexOf("Available Skills", StringComparison.Ordinal);
        var bundlesIdx = result.IndexOf("Skill Bundles", StringComparison.Ordinal);
        skillsIdx.ShouldBeGreaterThan(-1);
        bundlesIdx.ShouldBeGreaterThan(skillsIdx);
    }
}