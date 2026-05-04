// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Workflows;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Workflows;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="UnitValidationWorkflowScheduler"/>'s agent-runtime
/// id resolution (#1683). The fix routes the agent-runtime registry id
/// through <see cref="UnitExecutionDefaults.Agent"/> (sourced from the
/// manifest's <c>ai.agent</c> field) and only falls back to
/// <see cref="UnitExecutionDefaults.Runtime"/> /
/// <see cref="UnitExecutionDefaults.Provider"/> for back-compat with
/// units persisted before the slot existed.
/// </summary>
public class UnitValidationWorkflowSchedulerTests
{
    [Fact]
    public void ResolveAgentRuntimeId_PrefersAgent_OverRuntime()
    {
        // Pre-fix this would land on "podman" (the container-runtime
        // selector) and the workflow's RunContainerProbe would fail
        // every Step 1 with `No agent runtime is registered with id 'podman'.`
        var defaults = new UnitExecutionDefaults(
            Runtime: "podman",
            Agent: "claude");

        var runtimeId = UnitValidationWorkflowScheduler.ResolveAgentRuntimeId(defaults);

        runtimeId.ShouldBe("claude");
    }

    [Fact]
    public void ResolveAgentRuntimeId_FallsBackToRuntime_WhenAgentNull()
    {
        // Back-compat: a unit persisted before #1683 lacks the `agent`
        // slot, so the validator must keep working off `runtime` /
        // `provider` so existing units don't suddenly fail validation.
        var defaults = new UnitExecutionDefaults(
            Runtime: "ollama",
            Agent: null);

        var runtimeId = UnitValidationWorkflowScheduler.ResolveAgentRuntimeId(defaults);

        runtimeId.ShouldBe("ollama");
    }

    [Fact]
    public void ResolveAgentRuntimeId_FallsBackToProvider_WhenAgentAndRuntimeNull()
    {
        // Last-ditch: dapr-agent-style runtimes carry the same string in
        // their `provider` and `id` slots so a unit declaring only
        // `provider: openai` still resolves cleanly.
        var defaults = new UnitExecutionDefaults(
            Provider: "openai");

        var runtimeId = UnitValidationWorkflowScheduler.ResolveAgentRuntimeId(defaults);

        runtimeId.ShouldBe("openai");
    }

    [Fact]
    public void ResolveAgentRuntimeId_ReturnsNull_WhenAllThreeSlotsEmpty()
    {
        var defaults = new UnitExecutionDefaults();

        var runtimeId = UnitValidationWorkflowScheduler.ResolveAgentRuntimeId(defaults);

        runtimeId.ShouldBeNull();
    }

    [Fact]
    public void ResolveAgentRuntimeId_TreatsWhitespaceAgent_AsUnset()
    {
        // The store trims on read but a unit constructed in-memory by a
        // caller may still pass whitespace through; the resolver must
        // not treat that as a real agent-runtime id.
        var defaults = new UnitExecutionDefaults(
            Runtime: "podman",
            Agent: "   ");

        var runtimeId = UnitValidationWorkflowScheduler.ResolveAgentRuntimeId(defaults);

        runtimeId.ShouldBe("podman");
    }
}