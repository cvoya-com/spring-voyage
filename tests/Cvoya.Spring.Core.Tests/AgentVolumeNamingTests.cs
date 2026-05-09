// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests;

using Cvoya.Spring.Core.Execution;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AgentVolumeNaming"/> (D3c — ADR-0029).
/// Verifies the naming convention guarantees: stable identity, no cross-agent
/// collisions, runtime-safe identifiers, and bounded length.
/// </summary>
public class AgentVolumeNamingTests
{
    [Fact]
    public void ForAgent_SimpleId_PrefixedWithSpringWs()
    {
        var name = AgentVolumeNaming.ForAgent("my-agent");

        name.ShouldStartWith(AgentVolumeNaming.Prefix);
        name.ShouldBe("spring-ws-my-agent");
    }

    [Fact]
    public void ForAgent_SameAgentId_ReturnsSameVolumeName()
    {
        var name1 = AgentVolumeNaming.ForAgent("tenant-acme/agent-eng");
        var name2 = AgentVolumeNaming.ForAgent("tenant-acme/agent-eng");

        name1.ShouldBe(name2);
    }

    [Fact]
    public void ForAgent_DifferentAgentIds_ReturnDifferentNames()
    {
        var name1 = AgentVolumeNaming.ForAgent("agent-alpha");
        var name2 = AgentVolumeNaming.ForAgent("agent-beta");

        name1.ShouldNotBe(name2);
    }

    [Theory]
    [InlineData("tenants/acme/units/eng/agents/backend")]
    [InlineData("agent.with.dots")]
    [InlineData("UPPER_CASE_AGENT")]
    [InlineData("agent:with:colons")]
    [InlineData("agent with spaces")]
    [InlineData("agent__double__underscores")]
    public void ForAgent_VariousIds_ContainsOnlyLowercaseAlphanumericAndHyphens(string agentId)
    {
        var name = AgentVolumeNaming.ForAgent(agentId);

        // Drop the known-safe prefix; verify the rest
        name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
            .ShouldBeTrue($"Volume name '{name}' contains invalid characters");
    }

    [Fact]
    public void ForAgent_UpperCaseId_IsLowercased()
    {
        var name = AgentVolumeNaming.ForAgent("MyAgent");

        name.ShouldBe("spring-ws-myagent");
    }

    [Fact]
    public void ForAgent_SlashSeparatedPath_SlashesBecomeHyphens()
    {
        var name = AgentVolumeNaming.ForAgent("tenants/acme/agents/eng");

        name.ShouldBe("spring-ws-tenants-acme-agents-eng");
    }

    [Fact]
    public void ForAgent_ConsecutiveSpecialChars_CollapsedToSingleHyphen()
    {
        // Double underscore and dot together should collapse to a single hyphen.
        var name = AgentVolumeNaming.ForAgent("a..b__c");

        name.ShouldBe("spring-ws-a-b-c");
    }

    [Fact]
    public void ForAgent_LeadingAndTrailingSpecialChars_TrimmedFromSegment()
    {
        // The id segment after the prefix must not start or end with a hyphen.
        var name = AgentVolumeNaming.ForAgent("/leading/and/trailing/");

        name.ShouldNotEndWith("-");
        // The segment itself must not start with "-" (after the prefix).
        name[AgentVolumeNaming.Prefix.Length..].ShouldNotStartWith("-");
    }

    [Fact]
    public void ForAgent_VeryLongId_TruncatedBelowPodmanCap()
    {
        var longId = new string('a', 300);
        var name = AgentVolumeNaming.ForAgent(longId);

        // Total length must be under Podman/Docker's 255-char cap.
        name.Length.ShouldBeLessThanOrEqualTo(255);
        name.ShouldStartWith(AgentVolumeNaming.Prefix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ForAgent_NullOrWhitespace_Throws(string? agentId)
    {
        Should.Throw<ArgumentException>(() => AgentVolumeNaming.ForAgent(agentId!));
    }
}
