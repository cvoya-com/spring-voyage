// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.CommandLine;
using System.Linq;

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

    // --- ADR-0046 §3: multi-valued --roles / --expertise parsing ----------

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

    // --- ADR-0046 §3 / Phase 5 gaps — wire-shape mapping ------------------

    [Fact]
    public void FlattenMultiValued_MultiFlagMixedWithComma_MapsToWireShapeRolesArray()
    {
        // `spring unit members humans add --roles a,b --roles c` is the
        // canonical operator invocation. System.CommandLine collects
        // [`a,b`, `c`] into the option's string[]; the flattener emits the
        // exact `roles: ["a", "b", "c"]` JSON-array shape the
        // AddUnitHumanMemberRequest body expects (ADR-0046 §3 multi-valued).
        var result = UnitMembersCommand.FlattenMultiValued(new[] { "a,b", "c" });

        result.ShouldNotBeNull();
        result!.ShouldBe(new[] { "a", "b", "c" });
    }

    [Fact]
    public void FlattenMultiValued_ExplicitEmptyString_MapsToFullClearWireShape()
    {
        // ADR-0046 §5 full-replacement semantics: an explicit
        // `--roles ""` collapses to a single empty entry which the
        // flattener drops, producing a zero-element list. The action
        // layer's update path then forwards `roles: []` on the PATCH
        // body — the wire-side signal for "clear all roles".
        var result = UnitMembersCommand.FlattenMultiValued(new[] { string.Empty });

        result.ShouldNotBeNull();
        result!.ShouldBeEmpty();
    }

    [Fact]
    public void FlattenMultiValued_NullInput_MapsToLeaveUnchangedWireShape()
    {
        // The action layer keys "leave unchanged" off `null` (no flag
        // passed at all): the resulting PATCH body's Roles slot stays
        // null, which Kiota serialises as the absent property so the
        // backend takes the "no change" branch. This guards against a
        // future refactor that switches the missing-flag default to an
        // empty list (which would silently clear roles on every update).
        UnitMembersCommand.FlattenMultiValued(null).ShouldBeNull();
    }

    // --- #2463: agent / sub-unit member edit verb wiring -----------------

    [Fact]
    public void AgentsSubcommand_HasSetVerb_WithRolesAndExpertiseAndAgentOptions()
    {
        // `spring unit members agents set --unit <name> --agent <id>
        //   --roles ... --expertise ...` is the canonical shape from the
        // issue. This pins the verb tree + flag names so a future refactor
        // can't silently rename one of them and break operator muscle
        // memory + scripts.
        var outputOption = new Option<string>("--output");
        var agents = UnitMembersCommand.CreateAgentsSubcommand(outputOption);

        agents.Name.ShouldBe("agents");
        agents.Subcommands.Count.ShouldBe(1);

        var set = agents.Subcommands[0];
        set.Name.ShouldBe("set");
        set.Arguments.Any(a => a.Name == "unit").ShouldBeTrue();

        var optionNames = set.Options.Select(o => o.Name).ToHashSet();
        optionNames.ShouldContain("--agent");
        optionNames.ShouldContain("--roles");
        optionNames.ShouldContain("--expertise");
    }

    [Fact]
    public void SubUnitsSubcommand_HasSetVerb_WithRolesAndExpertiseAndSubUnitOption()
    {
        var outputOption = new Option<string>("--output");
        var subUnits = UnitMembersCommand.CreateSubUnitsSubcommand(outputOption);

        subUnits.Name.ShouldBe("units");
        subUnits.Subcommands.Count.ShouldBe(1);

        var set = subUnits.Subcommands[0];
        set.Name.ShouldBe("set");
        set.Arguments.Any(a => a.Name == "unit").ShouldBeTrue();

        var optionNames = set.Options.Select(o => o.Name).ToHashSet();
        optionNames.ShouldContain("--sub-unit");
        optionNames.ShouldContain("--roles");
        optionNames.ShouldContain("--expertise");
    }

    // --- ADR-0062 § 6 / #2808: --display-name + --as on humans add --------

    [Fact]
    public void HumansSubcommand_AddVerb_DeclaresDisplayNameAndAsFlags()
    {
        // ADR-0062 § 6 lands `spring unit members humans add
        // --display-name <...> --as <tenant-user-ref>` as the new
        // mint-and-attach path. Pin the flag surface so a future
        // refactor can't silently drop them and bypass the bound-set
        // resolver.
        var outputOption = new Option<string>("--output");
        var humans = UnitMembersCommand.CreateHumansSubcommand(outputOption);

        var add = humans.Subcommands.Single(c => c.Name == "add");
        var optionNames = add.Options.Select(o => o.Name).ToHashSet();
        optionNames.ShouldContain("--display-name");
        optionNames.ShouldContain("--as");
        optionNames.ShouldContain("--human");
        optionNames.ShouldContain("--roles");
    }

    [Fact]
    public void TryResolveTenantUserRefSync_Null_ResolvesToServerDefault()
    {
        // ADR-0062 § 1: when --as is omitted the CLI sends no override
        // and the server's ITenantUserDefaultResolver picks the
        // deployment default (OSS: operator; cloud: calling caller).
        var (resolved, value) = UnitMembersCommand.TryResolveTenantUserRefSync(null);
        resolved.ShouldBeTrue();
        value.ShouldBeNull();

        (resolved, value) = UnitMembersCommand.TryResolveTenantUserRefSync("   ");
        resolved.ShouldBeTrue();
        value.ShouldBeNull();
    }

    [Fact]
    public void TryResolveTenantUserRefSync_Me_ReturnsOssOperatorId()
    {
        // ADR-0062 § 6 + ADR-0047 §3: `--as me` resolves to the OSS
        // operator UUID in v0.1 (one tenant user per OSS deployment).
        // Cloud overlays plug a /me-equivalent in front (#2487 OUT1).
        var (resolved, value) = UnitMembersCommand.TryResolveTenantUserRefSync("me");
        resolved.ShouldBeTrue();
        value.ShouldBe(Cvoya.Spring.Core.Tenancy.OssTenantUserIds.Operator);

        (resolved, value) = UnitMembersCommand.TryResolveTenantUserRefSync("ME");
        resolved.ShouldBeTrue();
        value.ShouldBe(Cvoya.Spring.Core.Tenancy.OssTenantUserIds.Operator);
    }

    [Fact]
    public void TryResolveTenantUserRefSync_DashedGuid_Parses()
    {
        var input = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var (resolved, value) = UnitMembersCommand.TryResolveTenantUserRefSync(input);
        resolved.ShouldBeTrue();
        value.ShouldBe(System.Guid.Parse(input));
    }

    [Fact]
    public void TryResolveTenantUserRefSync_NoDashGuid_Parses()
    {
        // CONVENTIONS.md § Identifiers: lenient parse accepts both
        // dashed and no-dash hex forms.
        var hex = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var (resolved, value) = UnitMembersCommand.TryResolveTenantUserRefSync(hex);
        resolved.ShouldBeTrue();
        value.ShouldBe(System.Guid.Parse(hex));
    }

    [Fact]
    public void TryResolveTenantUserRefSync_OAuthSubject_FallsThroughToAsyncLookup()
    {
        // ADR-0062 § 6 / #2827: non-Guid non-`me` inputs need the
        // server-side OAuth-subject lookup. The sync helper reports
        // "not resolved" so the async wrapper drives the round-trip;
        // it does not raise (the lookup may legitimately succeed).
        var (resolved, value) = UnitMembersCommand.TryResolveTenantUserRefSync("alice@example.com");
        resolved.ShouldBeFalse();
        value.ShouldBeNull();
    }

    [Fact]
    public void TryResolveTenantUserRefSync_GuidEmpty_FallsThroughToAsyncLookup()
    {
        // Defensive: an all-zero Guid is the canonical "no value"
        // sentinel on the wire. The sync helper reports "not resolved"
        // so the OAuth-subject path runs — and the server returns 404
        // for a literal zero-Guid subject, surfacing a clean error.
        var (resolved, _) = UnitMembersCommand.TryResolveTenantUserRefSync(
            System.Guid.Empty.ToString());
        resolved.ShouldBeFalse();
    }
}
