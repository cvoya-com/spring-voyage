// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for the free helpers on <see cref="UnitMembersCommand"/>.
/// The action pipeline itself needs a live <c>SpringApiClient</c> so it
/// stays out of the test-class scope here — coverage of the wire-level
/// behaviour lives in <c>UnitTeamMembershipEndpointsTests</c>.
/// </summary>
public class UnitMembersCommandTests
{
    [Fact]
    public void ParseTagList_Null_ReturnsNullForNoChangeSemantics()
    {
        // Distinguishing "flag absent" (null) from "flag passed empty" (empty
        // list) is what lets the PATCH path tell "leave alone" from "clear".
        UnitMembersCommand.ParseTagList(null).ShouldBeNull();
    }

    [Fact]
    public void ParseTagList_Empty_ReturnsEmptyListForClearSemantics()
    {
        var result = UnitMembersCommand.ParseTagList(string.Empty);

        result.ShouldNotBeNull();
        result!.ShouldBeEmpty();
    }

    [Fact]
    public void ParseTagList_CommaSeparated_TrimsAndDropsBlanks()
    {
        var result = UnitMembersCommand.ParseTagList(" security , release-mgmt ,, ");

        result.ShouldNotBeNull();
        result!.ShouldBe(new[] { "security", "release-mgmt" });
    }

    [Fact]
    public void ParseTagList_SingleToken_ReturnsSingleElement()
    {
        UnitMembersCommand.ParseTagList("escalation")
            .ShouldBe(new[] { "escalation" });
    }
}
