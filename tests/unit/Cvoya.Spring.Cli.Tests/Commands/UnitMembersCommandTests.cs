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

    // --- ADR-0045 §3: multi-valued --roles / --expertise parsing ----------

    [Fact]
    public void FlattenMultiValued_Null_ReturnsNullForOmittedFlag()
    {
        // The action layer distinguishes "flag absent" (null) from "flag
        // passed empty" (empty list) for the update verb's tri-state.
        UnitMembersCommand.FlattenMultiValued(null).ShouldBeNull();
    }

    [Fact]
    public void FlattenMultiValued_EmptyArray_ReturnsEmptyList()
    {
        var result = UnitMembersCommand.FlattenMultiValued(Array.Empty<string>());

        result.ShouldNotBeNull();
        result!.ShouldBeEmpty();
    }

    [Fact]
    public void FlattenMultiValued_RepeatedTokens_ReturnsCombinedList()
    {
        // System.CommandLine collects --roles owner --roles reviewer into
        // ["owner", "reviewer"]; the flattener must preserve order.
        UnitMembersCommand.FlattenMultiValued(new[] { "owner", "reviewer" })
            .ShouldBe(new[] { "owner", "reviewer" });
    }

    [Fact]
    public void FlattenMultiValued_CommaSeparatedTokens_AreSplitAndTrimmed()
    {
        // The CLI accepts --roles "foo, bar,baz" as syntactic sugar for the
        // repeated form so scripts can pass a single comma-joined argument.
        UnitMembersCommand.FlattenMultiValued(new[] { "foo, bar,baz" })
            .ShouldBe(new[] { "foo", "bar", "baz" });
    }

    [Fact]
    public void FlattenMultiValued_MixedRepeatedAndComma_FlattensInOrder()
    {
        UnitMembersCommand.FlattenMultiValued(new[] { "owner", "reviewer,security_lead" })
            .ShouldBe(new[] { "owner", "reviewer", "security_lead" });
    }

    [Fact]
    public void FlattenMultiValued_EmptyTokenEntries_AreDropped()
    {
        // Defensive: --roles "" coalesces to a single empty entry on the
        // wire side of System.CommandLine. The flattener should swallow it
        // so callers can distinguish "passed empty" from "passed values".
        UnitMembersCommand.FlattenMultiValued(new[] { string.Empty })
            .ShouldBeEmpty();
    }
}
