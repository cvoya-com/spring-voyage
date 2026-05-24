// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Tests;

using Shouldly;

using Xunit;

/// <summary>
/// Parser tests for issue #2691 / #2696 — <c>execution.system_prompt_mode</c>
/// promoted to a first-class field on the unit, agent, agent-template, and
/// unit-template manifests so the in-tree package validator does not reject
/// the field as "unknown property". Covers:
/// <list type="bullet">
///   <item><description>Strict validation rejects unknown literals (<c>extend</c>, etc.) on every kind.</description></item>
///   <item><description>Valid literals (<c>append</c> / <c>replace</c>) accepted case-insensitively.</description></item>
///   <item><description>Parsed literals normalised to lower-case downstream.</description></item>
///   <item><description>Absent <c>execution:</c> or <c>execution.system_prompt_mode:</c> leaves the slot null
///       (resolution to the dispatcher's default <c>append</c> happens elsewhere).</description></item>
/// </list>
/// </summary>
public class SystemPromptModeFirstClassTests
{
    // ── Unit-level parser ─────────────────────────────────────────────────

    [Theory]
    [InlineData("append", "append")]
    [InlineData("replace", "replace")]
    [InlineData("Append", "append")]
    [InlineData("REPLACE", "replace")]
    public void Unit_ValidSystemPromptMode_AcceptedAndNormalisedToLowercase(string input, string expected)
    {
        var yaml = $$"""
            apiVersion: spring.voyage/v1
            kind: Unit
            name: u
            description: x
            execution:
              image: ghcr.io/example/u:latest
              system_prompt_mode: {{input}}
            """;

        var manifest = ManifestParser.Parse(yaml);

        manifest.Execution.ShouldNotBeNull();
        manifest.Execution!.SystemPromptMode.ShouldBe(expected);
    }

    [Theory]
    [InlineData("extend")]
    [InlineData("appendreplace")]
    [InlineData("override")]
    public void Unit_UnknownSystemPromptModeLiteral_Rejected(string input)
    {
        var yaml = $$"""
            apiVersion: spring.voyage/v1
            kind: Unit
            name: u
            description: x
            execution:
              system_prompt_mode: {{input}}
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.Parse(yaml));
        ex.Message.ShouldContain("execution.system_prompt_mode");
        ex.Message.ShouldContain(input);
        ex.Message.ShouldContain("#2691");
    }

    [Fact]
    public void Unit_ExecutionWithoutSystemPromptMode_SlotNull()
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
        manifest.Execution!.SystemPromptMode.ShouldBeNull();
    }

    // ── Agent-level parser ────────────────────────────────────────────────

    [Theory]
    [InlineData("append", "append")]
    [InlineData("replace", "replace")]
    [InlineData("APPEND", "append")]
    public void Agent_ValidSystemPromptMode_AcceptedAndNormalisedToLowercase(string input, string expected)
    {
        var yaml = $$"""
            apiVersion: spring.voyage/v1
            kind: Agent
            name: a
            description: x
            execution:
              image: ghcr.io/example/a:latest
              system_prompt_mode: {{input}}
            """;

        var manifest = ManifestParser.ParseAgent(yaml);

        manifest.Execution.ShouldNotBeNull();
        manifest.Execution!.SystemPromptMode.ShouldBe(expected);
    }

    [Theory]
    [InlineData("extend")]
    [InlineData("prepend")]
    public void Agent_UnknownSystemPromptModeLiteral_Rejected(string input)
    {
        var yaml = $$"""
            apiVersion: spring.voyage/v1
            kind: Agent
            name: a
            description: x
            execution:
              system_prompt_mode: {{input}}
            """;

        var ex = Should.Throw<ManifestParseException>(() => ManifestParser.ParseAgent(yaml));
        ex.Message.ShouldContain("execution.system_prompt_mode");
        ex.Message.ShouldContain(input);
    }

    // ── Agent-template parser ─────────────────────────────────────────────

    [Theory]
    [InlineData("append", "append")]
    [InlineData("Replace", "replace")]
    public void AgentTemplate_ValidSystemPromptMode_AcceptedAndNormalised(string input, string expected)
    {
        var yaml = $$"""
            apiVersion: spring.voyage/v1
            kind: AgentTemplate
            name: t
            description: x
            execution:
              system_prompt_mode: {{input}}
            """;

        var manifest = ManifestParser.ParseAgentTemplate(yaml);

        manifest.Execution.ShouldNotBeNull();
        manifest.Execution!.SystemPromptMode.ShouldBe(expected);
    }

    [Fact]
    public void AgentTemplate_UnknownSystemPromptMode_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: AgentTemplate
            name: t
            description: x
            execution:
              system_prompt_mode: extend
            """;

        var ex = Should.Throw<ManifestParseException>(
            () => ManifestParser.ParseAgentTemplate(yaml));
        ex.Message.ShouldContain("execution.system_prompt_mode");
        ex.Message.ShouldContain("extend");
    }

    // ── Unit-template parser ──────────────────────────────────────────────

    [Theory]
    [InlineData("append")]
    [InlineData("replace")]
    public void UnitTemplate_ValidSystemPromptMode_Accepted(string input)
    {
        var yaml = $$"""
            apiVersion: spring.voyage/v1
            kind: UnitTemplate
            name: t
            description: x
            execution:
              system_prompt_mode: {{input}}
            """;

        var manifest = ManifestParser.ParseUnitTemplate(yaml);

        manifest.Execution.ShouldNotBeNull();
        manifest.Execution!.SystemPromptMode.ShouldBe(input);
    }

    [Fact]
    public void UnitTemplate_UnknownSystemPromptMode_Rejected()
    {
        var yaml = """
            apiVersion: spring.voyage/v1
            kind: UnitTemplate
            name: t
            description: x
            execution:
              system_prompt_mode: extend
            """;

        var ex = Should.Throw<ManifestParseException>(
            () => ManifestParser.ParseUnitTemplate(yaml));
        ex.Message.ShouldContain("execution.system_prompt_mode");
    }

    // ── IsEmpty ──────────────────────────────────────────────────────────

    [Fact]
    public void ExecutionManifest_IsEmpty_ReturnsFalseWhenSystemPromptModeIsSet()
    {
        new ExecutionManifest { SystemPromptMode = "replace" }.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void AgentExecutionManifest_IsEmpty_ReturnsFalseWhenSystemPromptModeIsSet()
    {
        new AgentExecutionManifest { SystemPromptMode = "replace" }.IsEmpty.ShouldBeFalse();
    }
}
