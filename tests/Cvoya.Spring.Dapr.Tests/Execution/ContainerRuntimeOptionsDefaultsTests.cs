// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Dapr.Execution;

using Shouldly;

using Xunit;

/// <summary>
/// Pins the platform-default container runtime to <c>podman</c> (#1683).
/// Docker remains a supported value but is no longer the unconfigured
/// default — every shipped package YAML and any host that omits
/// <c>ContainerRuntime:RuntimeType</c> binds <c>podman</c> implicitly.
/// </summary>
public class ContainerRuntimeOptionsDefaultsTests
{
    [Fact]
    public void RuntimeType_DefaultsToPodman()
    {
        var options = new ContainerRuntimeOptions();

        options.RuntimeType.ShouldBe("podman");
    }
}