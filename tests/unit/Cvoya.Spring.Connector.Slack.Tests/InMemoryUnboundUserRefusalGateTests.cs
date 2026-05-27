// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using Cvoya.Spring.Connector.Slack.Inbound;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for <see cref="InMemoryUnboundUserRefusalGate"/> —
/// the once-per-session refusal idempotency per ADR-0061 §2.4.
/// </summary>
public class InMemoryUnboundUserRefusalGateTests
{
    [Fact]
    public void TryClaimRefusal_FirstCall_ReturnsTrue()
    {
        var sut = new InMemoryUnboundUserRefusalGate();
        sut.TryClaimRefusal("T-acme", "U-stranger").ShouldBeTrue();
    }

    [Fact]
    public void TryClaimRefusal_RepeatCallSameUser_ReturnsFalse()
    {
        var sut = new InMemoryUnboundUserRefusalGate();
        sut.TryClaimRefusal("T-acme", "U-stranger").ShouldBeTrue();
        sut.TryClaimRefusal("T-acme", "U-stranger").ShouldBeFalse();
        sut.TryClaimRefusal("T-acme", "U-stranger").ShouldBeFalse();
    }

    [Fact]
    public void TryClaimRefusal_DifferentUsers_BothGetRefused()
    {
        var sut = new InMemoryUnboundUserRefusalGate();
        sut.TryClaimRefusal("T-acme", "U-1").ShouldBeTrue();
        sut.TryClaimRefusal("T-acme", "U-2").ShouldBeTrue();
        sut.TryClaimRefusal("T-acme", "U-1").ShouldBeFalse();
    }

    [Fact]
    public void TryClaimRefusal_DifferentTeams_AreIsolated()
    {
        var sut = new InMemoryUnboundUserRefusalGate();
        sut.TryClaimRefusal("T-acme", "U-1").ShouldBeTrue();
        sut.TryClaimRefusal("T-other", "U-1").ShouldBeTrue();
    }
}
