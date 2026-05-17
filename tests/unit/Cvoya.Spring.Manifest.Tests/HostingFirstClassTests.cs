// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using Shouldly;

using Xunit;

/// <summary>
/// Parser tests for issue #2436 — <c>execution.hosting</c> promoted to a
/// first-class field on the unit, agent, agent-template, and unit-template
/// manifests. Covers:
/// <list type="bullet">
///   <item><description>Strict validation rejects unknown literals (<c>permanent</c>, etc.) on every kind.</description></item>
///   <item><description>Valid literals (<c>persistent</c> / <c>ephemeral</c> / <c>pooled</c>) accepted case-insensitively.</description></item>
///   <item><description>Parsed literals normalised to lower-case downstream.</description></item>
///   <item><description>Absent <c>execution:</c> or <c>execution.hosting:</c> leaves the slot null
///       (resolution to the dispatcher's default <c>persistent</c> happens elsewhere).</description></item>
/// </list>
/// </summary>
public class HostingFirstClassTests
{
    // ── Unit-level parser ─────────────────────────────────────────────────

    [Theory]
    [InlineData("persistent", "persistent")]
    [InlineData("ephemeral", "ephemeral")]
    [InlineData("pooled", "pooled")]
    [InlineData("Persistent", "persistent")]
    [InlineData("EPHEMERAL", "ephemeral")]
    [InlineData("Pooled", "pooled")]
    public void Unit_ValidHosting_AcceptedAndNormalisedToLowercase(string input, string expected)
    {
        var yaml = $$"""
            apiVersion: spring.voyage/v1
            kind: Unit
            name: u
            description: x
            execution:
              image: ghcr.io/example/u:latest
              hosting: {{input}}
            """;

        var manifest = ManifestParser.Parse(yaml);

        manifest.Execution.ShouldNotBeNull();
        manifest.Execution!.Hosting.ShouldBe(expected);
    }

    [Theory]
    [InlineData("permanent")]
    [InlineData("Persistent_X")]
    [InlineData("none")]
    [InlineData("Stateful")]
    public void Unit_UnknownHostingLiteral_Rejected(string input)
    {
        var yaml = $$"""
            apiVersion: spring.voyage/v1
            kind: Unit
            name: u
            description: x
            execution:
              hosting: {{input}}
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
        ex.Message.ShouldContain("execution.hosting");
        ex.Message.ShouldContain(input);
        ex.Message.ShouldContain("#2436");
    }

    [Fact]
    public void Unit_NoExecutionBlock_HostingNull()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: u
            description: x
            """;

        var manifest = ManifestParser.Parse(yaml);

        manifest.Execution.ShouldBeNull();
    }

    [Fact]
    public void Unit_ExecutionWithoutHosting_HostingNull()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Unit
            name: u
            description: x
            execution:
              image: ghcr.io/example/u:latest
            """;

        var manifest = ManifestParser.Parse(yaml);

        manifest.Execution.ShouldNotBeNull();
        manifest.Execution!.Hosting.ShouldBeNull();
    }

    // ── Agent-level parser ────────────────────────────────────────────────

    [Theory]
    [InlineData("persistent", "persistent")]
    [InlineData("ephemeral", "ephemeral")]
    [InlineData("pooled", "pooled")]
    [InlineData("PERSISTENT", "persistent")]
    public void Agent_ValidHosting_AcceptedAndNormalisedToLowercase(string input, string expected)
    {
        var yaml = $$"""
            apiVersion: spring.voyage/v1
            kind: Agent
            name: a
            description: x
            execution:
              image: ghcr.io/example/a:latest
              hosting: {{input}}
            """;

        var manifest = ManifestParser.ParseAgent(yaml);

        manifest.Execution.ShouldNotBeNull();
        manifest.Execution!.Hosting.ShouldBe(expected);
    }

    [Theory]
    [InlineData("permanent")]
    [InlineData("stateless")]
    [InlineData("warm")]
    public void Agent_UnknownHostingLiteral_Rejected(string input)
    {
        var yaml = $$"""
            apiVersion: spring.voyage/v1
            kind: Agent
            name: a
            description: x
            execution:
              hosting: {{input}}
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.ParseAgent(yaml));
        ex.Message.ShouldContain("execution.hosting");
        ex.Message.ShouldContain(input);
    }

    [Fact]
    public void Agent_NoExecutionBlock_HostingNull()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: Agent
            name: a
            description: x
            """;

        var manifest = ManifestParser.ParseAgent(yaml);

        manifest.Execution.ShouldBeNull();
    }

    // ── Agent-template parser ─────────────────────────────────────────────

    [Theory]
    [InlineData("persistent", "persistent")]
    [InlineData("EPHEMERAL", "ephemeral")]
    [InlineData("Pooled", "pooled")]
    public void AgentTemplate_ValidHosting_AcceptedAndNormalised(string input, string expected)
    {
        var yaml = $$"""
            apiVersion: spring.voyage/v1
            kind: AgentTemplate
            name: t
            description: x
            execution:
              hosting: {{input}}
            """;

        var manifest = ManifestParser.ParseAgentTemplate(yaml);

        manifest.Execution.ShouldNotBeNull();
        manifest.Execution!.Hosting.ShouldBe(expected);
    }

    [Fact]
    public void AgentTemplate_UnknownHosting_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: AgentTemplate
            name: t
            description: x
            execution:
              hosting: permanent
            """;

        var ex = Should.Throw<ManifestParseException>(
            () => ManifestParser.ParseAgentTemplate(yaml));
        ex.Message.ShouldContain("execution.hosting");
        ex.Message.ShouldContain("permanent");
    }

    // ── Unit-template parser ──────────────────────────────────────────────

    [Theory]
    [InlineData("persistent")]
    [InlineData("ephemeral")]
    [InlineData("pooled")]
    public void UnitTemplate_ValidHosting_Accepted(string input)
    {
        var yaml = $$"""
            apiVersion: spring.voyage/v1
            kind: UnitTemplate
            name: t
            description: x
            execution:
              hosting: {{input}}
            """;

        var manifest = ManifestParser.ParseUnitTemplate(yaml);

        manifest.Execution.ShouldNotBeNull();
        manifest.Execution!.Hosting.ShouldBe(input);
    }

    [Fact]
    public void UnitTemplate_UnknownHosting_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitTemplate
            name: t
            description: x
            execution:
              hosting: permanent
            """;

        var ex = Should.Throw<ManifestParseException>(
            () => ManifestParser.ParseUnitTemplate(yaml));
        ex.Message.ShouldContain("execution.hosting");
    }

    // ── ExecutionManifest.IsEmpty ─────────────────────────────────────────

    [Fact]
    public void ExecutionManifest_IsEmpty_ReturnsTrueWhenEveryFieldIsBlank()
    {
        new ExecutionManifest().IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void ExecutionManifest_IsEmpty_ReturnsFalseWhenHostingIsSet()
    {
        new ExecutionManifest { Hosting = "persistent" }.IsEmpty.ShouldBeFalse();
    }

    // ── AgentExecutionManifest.IsEmpty ────────────────────────────────────

    [Fact]
    public void AgentExecutionManifest_IsEmpty_ReturnsTrueWhenEveryFieldIsBlank()
    {
        new AgentExecutionManifest().IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void AgentExecutionManifest_IsEmpty_ReturnsFalseWhenHostingIsSet()
    {
        new AgentExecutionManifest { Hosting = "ephemeral" }.IsEmpty.ShouldBeFalse();
    }
}
