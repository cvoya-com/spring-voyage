// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Tools;

using System.Text.Json;

using Cvoya.Spring.Core.Tools;
using Cvoya.Spring.Dapr.Tools;

using FluentAssertions;

using NSubstitute;

using Xunit;

/// <summary>
/// Unit tests for <see cref="PlatformToolRegistry"/>.
/// </summary>
public class PlatformToolRegistryTests
{
    [Fact]
    public void Register_AndGet_RoundTrips()
    {
        var registry = new PlatformToolRegistry();
        var tool = Substitute.For<IPlatformTool>();
        tool.Name.Returns("testTool");

        registry.Register(tool);

        var retrieved = registry.Get("testTool");
        retrieved.Should().BeSameAs(tool);
    }

    [Fact]
    public void Get_UnknownTool_ReturnsNull()
    {
        var registry = new PlatformToolRegistry();

        var retrieved = registry.Get("nonexistent");

        retrieved.Should().BeNull();
    }

    [Fact]
    public void GetAll_ReturnsAllRegisteredTools()
    {
        var registry = new PlatformToolRegistry();
        var tool1 = Substitute.For<IPlatformTool>();
        tool1.Name.Returns("tool1");
        var tool2 = Substitute.For<IPlatformTool>();
        tool2.Name.Returns("tool2");

        registry.Register(tool1);
        registry.Register(tool2);

        var all = registry.GetAll();
        all.Should().HaveCount(2);
        all.Should().Contain(tool1);
        all.Should().Contain(tool2);
    }
}