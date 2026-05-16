// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Workflows;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Workflows;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="ArtefactValidationWorkflowScheduler"/>'s agent-runtime
/// id resolution (#1683). The fix routes the agent-runtime registry id
/// through <see cref="UnitExecutionDefaults.Agent"/> (sourced from the
/// manifest's <c>ai.agent</c> field) and only falls back to
/// <see cref="UnitExecutionDefaults.Provider"/> for back-compat with
/// units persisted before the slot existed.
/// </summary>
public class ArtefactValidationWorkflowSchedulerTests
{
    [Fact]
    public void ResolveAgentRuntimeId_PrefersAgent_OverProvider()
    {
        var defaults = new UnitExecutionDefaults(
            Provider: "openai",
            Agent: "claude");

        var runtimeId = ArtefactValidationWorkflowScheduler.ResolveAgentRuntimeId(defaults.Agent, defaults.Provider);

        runtimeId.ShouldBe("claude");
    }

    [Fact]
    public void ResolveAgentRuntimeId_FallsBackToProvider_WhenAgentAndRuntimeNull()
    {
        // Last-ditch: dapr-agent-style runtimes carry the same string in
        // their `provider` and `id` slots so a unit declaring only
        // `provider: openai` still resolves cleanly.
        var defaults = new UnitExecutionDefaults(
            Provider: "openai");

        var runtimeId = ArtefactValidationWorkflowScheduler.ResolveAgentRuntimeId(defaults.Agent, defaults.Provider);

        runtimeId.ShouldBe("openai");
    }

    [Fact]
    public void ResolveAgentRuntimeId_ReturnsNull_WhenSlotsEmpty()
    {
        var defaults = new UnitExecutionDefaults();

        var runtimeId = ArtefactValidationWorkflowScheduler.ResolveAgentRuntimeId(defaults.Agent, defaults.Provider);

        runtimeId.ShouldBeNull();
    }

    [Fact]
    public void ResolveAgentRuntimeId_TreatsWhitespaceAgent_AsUnset_FallsToProvider()
    {
        // Whitespace Agent is treated as unset; Provider is used as a
        // back-compat fallback.
        var defaults = new UnitExecutionDefaults(
            Provider: "ollama",
            Agent: "   ");

        var runtimeId = ArtefactValidationWorkflowScheduler.ResolveAgentRuntimeId(defaults.Agent, defaults.Provider);

        runtimeId.ShouldBe("ollama");
    }
}
