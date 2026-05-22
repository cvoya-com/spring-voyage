// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DbAgentDefinitionProvider.Merge"/> — the
/// field-level precedence rule behind the B-wide execution inheritance
/// model (#601 / #603 / #409).
/// </summary>
/// <remarks>
/// ADR-0038 amendment (#2634): the execution config is the one canonical
/// shape <c>(runtime, model{provider, id}, image, hosting)</c>; the merge
/// resolves the runtime id (<c>agent.Runtime → unit.Runtime → null</c>).
/// </remarks>
public class DbAgentDefinitionProviderMergeTests
{
    [Fact]
    public void Merge_AgentWins_OnEveryField()
    {
        var agent = new AgentExecutionConfig(
            Runtime: "claude-code",
            Image: "agent-img",
            Hosting: AgentHostingMode.Persistent,
            Model: new Model("anthropic", "claude-sonnet"));
        var unit = new UnitExecutionDefaults(
            Image: "unit-img",
            Model: new Model("openai", "gpt-4o"),
            Runtime: "spring-voyage");

        var merged = DbAgentDefinitionProvider.Merge(agent, unit);

        merged.ShouldNotBeNull();
        merged!.Runtime.ShouldBe("claude-code");
        merged.Image.ShouldBe("agent-img");
        merged.Model.ShouldBe(new Model("anthropic", "claude-sonnet"));
        merged.Hosting.ShouldBe(AgentHostingMode.Persistent);
    }

    [Fact]
    public void Merge_UnitFillsIn_MissingAgentFields()
    {
        var agent = new AgentExecutionConfig(
            Runtime: "claude-code",
            Image: null,      // missing
            Hosting: AgentHostingMode.Ephemeral,
            Model: null);
        var unit = new UnitExecutionDefaults(
            Image: "unit-img",
            Model: new Model("openai", "gpt-4o"),
            Runtime: "spring-voyage");    // ignored — agent wins on Runtime

        var merged = DbAgentDefinitionProvider.Merge(agent, unit);

        merged.ShouldNotBeNull();
        merged!.Runtime.ShouldBe("claude-code");
        merged.Image.ShouldBe("unit-img");
        merged.Model.ShouldBe(new Model("openai", "gpt-4o"));
    }

    [Fact]
    public void Merge_AgentNull_UnitProvidesRuntime_UsesUnit()
    {
        var unit = new UnitExecutionDefaults(
            Image: "unit-img",
            Runtime: "claude-code");

        var merged = DbAgentDefinitionProvider.Merge(null, unit);

        merged.ShouldNotBeNull();
        merged!.Runtime.ShouldBe("claude-code");
        merged.Image.ShouldBe("unit-img");
        merged.Hosting.ShouldBe(AgentHostingMode.Persistent);
    }

    [Fact]
    public void Merge_ReturnsNull_WhenNeitherSideProvidesRuntime()
    {
        var agent = new AgentExecutionConfig(Runtime: "", Image: null);
        var unit = new UnitExecutionDefaults(Image: "unit-img");

        var merged = DbAgentDefinitionProvider.Merge(agent, unit);

        merged.ShouldBeNull();
    }

    [Fact]
    public void Merge_HostingIsAgentOwned_UnitNeverChangesIt()
    {
        var agent = new AgentExecutionConfig(
            Runtime: "claude-code",
            Image: "x",
            Hosting: AgentHostingMode.Persistent);
        var unit = new UnitExecutionDefaults();

        var merged = DbAgentDefinitionProvider.Merge(agent, unit);

        merged.ShouldNotBeNull();
        merged!.Hosting.ShouldBe(AgentHostingMode.Persistent);
    }
}
