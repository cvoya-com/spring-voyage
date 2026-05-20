// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Orchestration;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="DirectoryOrchestrationToolProvider"/>. Pins the
/// "any agent:// or unit:// address gets the closed two-tool set;
/// other schemes get an empty array" contract from ADR-0039 §3
/// (as amended 2026-05-19, #2536 / #2537). Membership is not a gate;
/// discovery / inspection / status tools live on the <c>sv.*</c> directory
/// surface, not the orchestration surface.
/// </summary>
public class DirectoryOrchestrationToolProviderTests
{
    private static readonly Guid SomeId = new("aaaaaaaa-0000-0000-0000-000000000001");

    private DirectoryOrchestrationToolProvider CreateProvider() => new();

    private static readonly OrchestrationToolName[] ExpectedToolset =
    [
        OrchestrationToolName.DelegateTo,
        OrchestrationToolName.FanoutTo,
    ];

    [Fact]
    public void GetOrchestrationTools_AgentAddress_ReturnsTwoDescriptors()
    {
        // Per the 2026-05-19 amendment to ADR-0039 §3 (#2536 / #2537), entity
        // type is not a gate and membership is not a gate. Any agent://
        // address gets the toolset unconditionally; the surface is the two
        // action verbs only.
        var provider = CreateProvider();
        var address = new Address(Address.AgentScheme, SomeId);

        var tools = provider.GetOrchestrationTools(address, Guid.NewGuid());

        tools.Length.ShouldBe(2);
        tools.Select(t => t.Name).ShouldBe(ExpectedToolset);
    }

    [Fact]
    public void GetOrchestrationTools_UnitAddress_ReturnsTwoDescriptors()
    {
        var provider = CreateProvider();
        var address = new Address(Address.UnitScheme, SomeId);

        var tools = provider.GetOrchestrationTools(address, Guid.NewGuid());

        tools.Length.ShouldBe(2);
        tools.Select(t => t.Name).ShouldBe(ExpectedToolset);

        // Each descriptor must carry both schemas as concrete JSON objects;
        // ADR-0039 §3 advertises them through the launcher's tool surface.
        foreach (var descriptor in tools)
        {
            descriptor.InputSchema.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object);
            descriptor.OutputSchema.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object);
        }
    }

    [Fact]
    public void GetOrchestrationTools_HumanScheme_ReturnsEmpty()
    {
        // Non-addressable schemes (human, connector) are not orchestration
        // callers; the provider returns empty so launchers do not attach
        // the toolset for them.
        var provider = CreateProvider();
        var address = new Address(Address.HumanScheme, SomeId);

        var tools = provider.GetOrchestrationTools(address, Guid.NewGuid());

        tools.ShouldBeEmpty();
    }
}
