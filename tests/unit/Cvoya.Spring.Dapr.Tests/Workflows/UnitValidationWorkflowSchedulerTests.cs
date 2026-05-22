// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Workflows;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for the unit-level execution defaults consumed by
/// <see cref="Cvoya.Spring.Dapr.Workflows.ArtefactValidationWorkflowScheduler"/>.
/// Per the ADR-0038 amendment (#2634) the agent-runtime registry id is the
/// dedicated <see cref="UnitExecutionDefaults.Runtime"/> slot — there is no
/// provider fallback; runtime and model are distinct fields.
/// </summary>
public class ArtefactValidationWorkflowSchedulerTests
{
    [Fact]
    public void Runtime_IsTheDedicatedSlot()
    {
        var defaults = new UnitExecutionDefaults(
            Model: new Model("openai", "gpt-4o"),
            Runtime: "spring-voyage");

        defaults.Runtime.ShouldBe("spring-voyage");
        defaults.Model.ShouldBe(new Model("openai", "gpt-4o"));
    }

    [Fact]
    public void Runtime_IsNull_WhenUnset()
    {
        var defaults = new UnitExecutionDefaults(Model: new Model("openai", "gpt-4o"));

        defaults.Runtime.ShouldBeNull();
    }

    [Fact]
    public void IsEmpty_WhenAllSlotsUnset()
    {
        new UnitExecutionDefaults().IsEmpty.ShouldBeTrue();
    }
}
