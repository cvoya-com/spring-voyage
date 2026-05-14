// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Orchestration;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="DirectoryOrchestrationToolProvider"/>. Pins the
/// "leaf agents get no tools, units with at least one child get the closed
/// five-tool set" contract from ADR-0039 §3.
/// </summary>
/// <remarks>
/// #2081: the provider now reads members directly from
/// <see cref="IUnitMemberGraphStore"/> rather than calling
/// <c>IUnitActor.GetMembersAsync</c> through a Dapr actor proxy — the
/// pre-fix path deadlocked when the provider was invoked from inside a
/// <c>UnitActor</c> turn. The tests substitute the store directly.
/// </remarks>
public class DirectoryOrchestrationToolProviderTests
{
    private static readonly Guid LeafAgentId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OneChildUnitId = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid ThreeChildrenUnitId = new("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly IUnitMemberGraphStore _memberGraphStore = Substitute.For<IUnitMemberGraphStore>();
    private readonly ILogger<DirectoryOrchestrationToolProvider> _logger =
        Substitute.For<ILogger<DirectoryOrchestrationToolProvider>>();

    private readonly Dictionary<Guid, IReadOnlyList<Address>> _members = new();

    public DirectoryOrchestrationToolProviderTests()
    {
        _memberGraphStore
            .GetMembersAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var unitId = ci.ArgAt<Guid>(0);
                return _members.TryGetValue(unitId, out var m)
                    ? m
                    : (IReadOnlyList<Address>)Array.Empty<Address>();
            });
    }

    private DirectoryOrchestrationToolProvider CreateProvider() =>
        new(_memberGraphStore, _logger);

    [Fact]
    public void GetOrchestrationTools_LeafAgent_ReturnsEmpty()
    {
        var provider = CreateProvider();
        var leaf = new Address(Address.AgentScheme, LeafAgentId);

        var tools = provider.GetOrchestrationTools(leaf, Guid.NewGuid());

        tools.ShouldBeEmpty();
    }

    [Fact]
    public void GetOrchestrationTools_UnitWithOneChild_ReturnsFiveDescriptors()
    {
        _members[OneChildUnitId] = new[]
        {
            new Address(Address.AgentScheme, Guid.NewGuid()),
        };
        var provider = CreateProvider();
        var unit = new Address(Address.UnitScheme, OneChildUnitId);

        var tools = provider.GetOrchestrationTools(unit, Guid.NewGuid());

        tools.Length.ShouldBe(5);
        tools.Select(t => t.Name).ShouldBe(new[]
        {
            OrchestrationToolName.ListChildren,
            OrchestrationToolName.InspectChild,
            OrchestrationToolName.DelegateToChild,
            OrchestrationToolName.FanoutToChildren,
            OrchestrationToolName.QueryChildStatus,
        });

        // Each descriptor must carry both schemas as concrete JSON objects;
        // ADR-0039 §3 advertises them through the launcher's tool surface.
        foreach (var descriptor in tools)
        {
            descriptor.InputSchema.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object);
            descriptor.OutputSchema.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object);
        }
    }

    [Fact]
    public void GetOrchestrationTools_UnitWithThreeChildren_ReturnsSameFiveDescriptors()
    {
        _members[ThreeChildrenUnitId] = new[]
        {
            new Address(Address.AgentScheme, Guid.NewGuid()),
            new Address(Address.AgentScheme, Guid.NewGuid()),
            new Address(Address.UnitScheme, Guid.NewGuid()),
        };
        var provider = CreateProvider();
        var unit = new Address(Address.UnitScheme, ThreeChildrenUnitId);

        var tools = provider.GetOrchestrationTools(unit, Guid.NewGuid());

        // The descriptor set is static — it does not scale with the child
        // count. The five canonical tools are always present when the unit
        // has at least one child.
        tools.Length.ShouldBe(5);
        tools.Select(t => t.Name).ShouldBe(new[]
        {
            OrchestrationToolName.ListChildren,
            OrchestrationToolName.InspectChild,
            OrchestrationToolName.DelegateToChild,
            OrchestrationToolName.FanoutToChildren,
            OrchestrationToolName.QueryChildStatus,
        });
    }
}
