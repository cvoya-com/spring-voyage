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
/// "addresses with at least one child get the closed five-tool set;
/// addresses with no children get an empty array" contract from ADR-0039 §3
/// (as amended 2026-05-19). Entity type is not a gate.
/// </summary>
/// <remarks>
/// #2081: the provider reads members directly from
/// <see cref="IUnitMemberGraphStore"/> rather than calling
/// <c>IUnitActor.GetMembersAsync</c> through a Dapr actor proxy — the
/// pre-fix path deadlocked when the provider was invoked from inside a
/// <c>UnitActor</c> turn. The tests substitute the store directly.
/// </remarks>
public class DirectoryOrchestrationToolProviderTests
{
    private static readonly Guid EmptyAgentId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid AgentWithChildrenId = new("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid OneChildUnitId = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid ThreeChildrenUnitId = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid EmptyUnitId = new("bbbbbbbb-0000-0000-0000-000000000003");

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

    private static readonly OrchestrationToolName[] ExpectedToolset =
    [
        OrchestrationToolName.ListChildren,
        OrchestrationToolName.InspectChild,
        OrchestrationToolName.DelegateToChild,
        OrchestrationToolName.FanoutToChildren,
        OrchestrationToolName.QueryChildStatus,
    ];

    [Fact]
    public void GetOrchestrationTools_AgentWithNoChildren_ReturnsEmpty()
    {
        var provider = CreateProvider();
        var address = new Address(Address.AgentScheme, EmptyAgentId);

        var tools = provider.GetOrchestrationTools(address, Guid.NewGuid());

        tools.ShouldBeEmpty();
    }

    [Fact]
    public void GetOrchestrationTools_UnitWithNoChildren_ReturnsEmpty()
    {
        var provider = CreateProvider();
        var address = new Address(Address.UnitScheme, EmptyUnitId);

        var tools = provider.GetOrchestrationTools(address, Guid.NewGuid());

        tools.ShouldBeEmpty();
    }

    [Fact]
    public void GetOrchestrationTools_AgentWithChildren_ReturnsFiveDescriptors()
    {
        // Per the 2026-05-19 amendment to ADR-0039 §3, entity type is not a
        // gate. If the membership graph records children for an `agent://`
        // address, the toolset is attached.
        _members[AgentWithChildrenId] = new[]
        {
            new Address(Address.AgentScheme, Guid.NewGuid()),
        };
        var provider = CreateProvider();
        var address = new Address(Address.AgentScheme, AgentWithChildrenId);

        var tools = provider.GetOrchestrationTools(address, Guid.NewGuid());

        tools.Length.ShouldBe(5);
        tools.Select(t => t.Name).ShouldBe(ExpectedToolset);
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
        // count. The five canonical tools are always present when the
        // address has at least one child.
        tools.Length.ShouldBe(5);
        tools.Select(t => t.Name).ShouldBe(ExpectedToolset);
    }
}
