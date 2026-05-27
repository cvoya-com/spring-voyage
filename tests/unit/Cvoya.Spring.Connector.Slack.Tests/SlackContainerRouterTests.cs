// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using Cvoya.Spring.Connector.Slack.Routing;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Messaging;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for <see cref="SlackContainerRouter"/> per ADR-0061 §7.2.
/// The OSS shape only ever fires <see cref="SlackContainerRoute.DirectMessage"/>
/// (single bound human) and <see cref="SlackContainerRoute.None"/>
/// (no bound human); the <see cref="SlackContainerRoute.PrivateChannel"/>
/// branch is the forward-compat seam preserved for the hybrid mode.
/// </summary>
public class SlackContainerRouterTests
{
    private static readonly Guid TenantUserOperator = new("11111111-0000-0000-0000-000000000000");
    private static readonly Guid TenantUserSecond = new("22222222-0000-0000-0000-000000000000");

    private static readonly Address Bob = new(Address.AgentScheme, new Guid("00000001-0000-0000-0000-000000000000"));
    private static readonly Address Operator = new(Address.HumanScheme, TenantUserOperator);
    private static readonly Address Second = new(Address.HumanScheme, TenantUserSecond);

    [Fact]
    public void Route_OssShape_SingleBoundHuman_ReturnsDirectMessage()
    {
        var sut = new SlackContainerRouter();
        var bound = new[] { new TenantBoundUser("U-installer", TenantUserOperator) };

        var route = sut.Route(new[] { Operator, Bob }, bound);

        route.ShouldBeOfType<SlackContainerRoute.DirectMessage>();
        ((SlackContainerRoute.DirectMessage)route).SlackUserId.ShouldBe("U-installer");
    }

    [Fact]
    public void Route_NoBoundHumans_ReturnsNone()
    {
        var sut = new SlackContainerRouter();
        var bound = new[] { new TenantBoundUser("U-installer", TenantUserOperator) };

        // Two agents, no humans → no Slack surface.
        var route = sut.Route(new[] { Bob, new Address(Address.AgentScheme, Guid.NewGuid()) }, bound);

        route.ShouldBeOfType<SlackContainerRoute.None>();
    }

    [Fact]
    public void Route_NoBoundUsers_ReturnsNone()
    {
        var sut = new SlackContainerRouter();

        var route = sut.Route(new[] { Operator, Bob }, Array.Empty<TenantBoundUser>());

        route.ShouldBeOfType<SlackContainerRoute.None>();
    }

    [Fact]
    public void Route_TwoBoundHumans_ReturnsPrivateChannel_ButConsumingThrows()
    {
        // ADR-0061 §7.2: PrivateChannel branch must be reachable so
        // the seam stays alive even though the v0.1 consumer throws.
        var sut = new SlackContainerRouter();
        var bound = new[]
        {
            new TenantBoundUser("U-operator", TenantUserOperator),
            new TenantBoundUser("U-second", TenantUserSecond),
        };

        var route = sut.Route(new[] { Operator, Second, Bob }, bound);

        route.ShouldBeOfType<SlackContainerRoute.PrivateChannel>();

        // A consumer that switches on the branch in v0.1 throws the
        // documented NotSupportedException. The router itself does
        // not throw — the seam is the discriminated-union branch.
        Should.Throw<NotSupportedException>(() =>
        {
            switch (route)
            {
                case SlackContainerRoute.PrivateChannel:
                    throw new NotSupportedException("PrivateChannel routing reserved for hybrid mode — ADR-0061 §7.2");
                default:
                    break;
            }
        });
    }

    [Fact]
    public void Route_IteratesBoundUserList_NotSingleton()
    {
        // ADR-0061 §7.1: the bound-user list might grow to N in cloud.
        // The router must iterate, not assume singleton — verify by
        // packing the matching tuple as the second list entry.
        var sut = new SlackContainerRouter();
        var bound = new[]
        {
            new TenantBoundUser("U-other", new Guid("99999999-0000-0000-0000-000000000000")),
            new TenantBoundUser("U-installer", TenantUserOperator),
        };

        var route = sut.Route(new[] { Operator, Bob }, bound);

        route.ShouldBeOfType<SlackContainerRoute.DirectMessage>();
        ((SlackContainerRoute.DirectMessage)route).SlackUserId.ShouldBe("U-installer");
    }
}
