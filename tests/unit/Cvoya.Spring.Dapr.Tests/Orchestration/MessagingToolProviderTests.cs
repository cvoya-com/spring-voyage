// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Orchestration;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="MessagingToolProvider"/>. Pins the "any agent:// or
/// unit:// address gets the closed two-tool messaging set; other schemes
/// get an empty array" contract (ADR-0048 / ADR-0049). The platform
/// delivers messages; discovery / inspection / status tools live on the
/// <c>sv.directory.*</c> surface, not the messaging surface.
/// </summary>
public class MessagingToolProviderTests
{
    private static readonly Guid SomeId = new("aaaaaaaa-0000-0000-0000-000000000001");

    private static MessagingToolProvider CreateProvider() => new();

    private static readonly MessagingToolName[] ExpectedToolset =
    [
        MessagingToolName.Send,
        MessagingToolName.Broadcast,
    ];

    [Fact]
    public void GetMessagingTools_AgentAddress_ReturnsTwoDescriptors()
    {
        // The messaging tools are available to every agent / unit caller —
        // there is no membership gate and no entity-type gate.
        var provider = CreateProvider();
        var address = new Address(Address.AgentScheme, SomeId);

        var tools = provider.GetMessagingTools(address, Guid.NewGuid());

        tools.Length.ShouldBe(2);
        tools.Select(t => t.Name).ShouldBe(ExpectedToolset);
    }

    [Fact]
    public void GetMessagingTools_UnitAddress_ReturnsTwoDescriptors()
    {
        var provider = CreateProvider();
        var address = new Address(Address.UnitScheme, SomeId);

        var tools = provider.GetMessagingTools(address, Guid.NewGuid());

        tools.Length.ShouldBe(2);
        tools.Select(t => t.Name).ShouldBe(ExpectedToolset);

        // Each descriptor must carry both schemas as concrete JSON objects;
        // they are advertised through the launcher's tool surface.
        foreach (var descriptor in tools)
        {
            descriptor.InputSchema.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object);
            descriptor.OutputSchema.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Object);
        }
    }

    [Fact]
    public void GetMessagingTools_HumanScheme_ReturnsEmpty()
    {
        // Non-messaging schemes (human, connector) are not messaging
        // callers; the provider returns empty so launchers do not attach
        // the toolset for them.
        var provider = CreateProvider();
        var address = new Address(Address.HumanScheme, SomeId);

        var tools = provider.GetMessagingTools(address, Guid.NewGuid());

        tools.ShouldBeEmpty();
    }
}
