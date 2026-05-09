// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

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
/// #1732: <c>Tool</c> was dropped from <see cref="AgentExecutionConfig"/>
/// and <see cref="UnitExecutionDefaults"/>. The execution tool is derived
/// from <see cref="AgentExecutionConfig.AgentRuntimeId"/> via the
/// catalogue runtime's <c>Launcher</c> field; the merge now resolves the
/// runtime id (<c>agent.AgentRuntimeId → unit.Agent → null</c>).
/// </remarks>
public class DbAgentDefinitionProviderMergeTests
{
    [Fact]
    public void Merge_AgentWins_OnEveryField()
    {
        var agent = new AgentExecutionConfig(
            AgentRuntimeId: "claude",
            Image: "agent-img",
            Hosting: AgentHostingMode.Persistent,
            Provider: "anthropic",
            Model: "claude-sonnet");
        var unit = new UnitExecutionDefaults(
            Image: "unit-img",
            Provider: "openai",
            Model: "gpt-4o",
            Agent: "openai");

        var merged = DbAgentDefinitionProvider.Merge(agent, unit);

        merged.ShouldNotBeNull();
        merged!.AgentRuntimeId.ShouldBe("claude");
        merged.Image.ShouldBe("agent-img");
        merged.Provider.ShouldBe("anthropic");
        merged.Model.ShouldBe("claude-sonnet");
        merged.Hosting.ShouldBe(AgentHostingMode.Persistent);
    }

    [Fact]
    public void Merge_UnitFillsIn_MissingAgentFields()
    {
        var agent = new AgentExecutionConfig(
            AgentRuntimeId: "claude",
            Image: null,      // missing
            Hosting: AgentHostingMode.Ephemeral,
            Provider: null,
            Model: null);
        var unit = new UnitExecutionDefaults(
            Image: "unit-img",
            Provider: "openai",
            Model: "gpt-4o",
            Agent: "openai");    // ignored — agent wins on AgentRuntimeId

        var merged = DbAgentDefinitionProvider.Merge(agent, unit);

        merged.ShouldNotBeNull();
        merged!.AgentRuntimeId.ShouldBe("claude");
        merged.Image.ShouldBe("unit-img");
        merged.Provider.ShouldBe("openai");
        merged.Model.ShouldBe("gpt-4o");
    }

    [Fact]
    public void Merge_AgentNull_UnitProvidesAgentRuntimeId_UsesUnit()
    {
        var unit = new UnitExecutionDefaults(
            Image: "unit-img",
            Agent: "claude");

        var merged = DbAgentDefinitionProvider.Merge(null, unit);

        merged.ShouldNotBeNull();
        merged!.AgentRuntimeId.ShouldBe("claude");
        merged.Image.ShouldBe("unit-img");
        merged.Hosting.ShouldBe(AgentHostingMode.Ephemeral);
    }

    [Fact]
    public void Merge_ReturnsNull_WhenNeitherSideProvidesAgentRuntimeId()
    {
        var agent = new AgentExecutionConfig(AgentRuntimeId: "", Image: null);
        var unit = new UnitExecutionDefaults(Image: "unit-img");

        var merged = DbAgentDefinitionProvider.Merge(agent, unit);

        merged.ShouldBeNull();
    }

    [Fact]
    public void Merge_HostingIsAgentOwned_UnitNeverChangesIt()
    {
        var agent = new AgentExecutionConfig(
            AgentRuntimeId: "claude",
            Image: "x",
            Hosting: AgentHostingMode.Persistent);
        var unit = new UnitExecutionDefaults();

        var merged = DbAgentDefinitionProvider.Merge(agent, unit);

        merged.ShouldNotBeNull();
        merged!.Hosting.ShouldBe(AgentHostingMode.Persistent);
    }
}
