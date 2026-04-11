// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Workflows;

using Cvoya.Spring.Dapr.Workflows;

using FluentAssertions;

using global::Dapr.Workflow;

using NSubstitute;

using Xunit;

/// <summary>
/// Unit tests for <see cref="CloningLifecycleWorkflow"/>.
/// </summary>
public class CloningLifecycleWorkflowTests
{
    [Fact]
    public async Task RunAsync_ReturnsNotImplemented()
    {
        var workflow = new CloningLifecycleWorkflow();
        var context = Substitute.For<WorkflowContext>();
        var input = new CloningInput("source-agent", "target-agent");

        var result = await workflow.RunAsync(context, input);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not implemented");
    }
}