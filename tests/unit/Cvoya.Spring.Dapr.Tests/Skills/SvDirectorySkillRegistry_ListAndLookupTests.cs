// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for the ADR-0056 §8 fundamental-core directory tools on
/// <see cref="SvDirectorySkillRegistry"/> (#2656), as consolidated by #3069:
/// <see cref="SvDirectorySkillRegistry.ListTool"/> (the single member-listing
/// surface — scope, explicit unit uuid, human folding, role / expertise
/// filter, pagination, policy gate) and
/// <see cref="SvDirectorySkillRegistry.LookupTool"/> (resolve one entry by
/// address OR uuid).
/// </summary>
public class SvDirectorySkillRegistry_ListAndLookupTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-1111-1111-1111-000000000ff1");
    private static readonly Guid ParentUnitId = Guid.Parse("bbbbbbbb-2222-2222-2222-000000000ff1");
    private static readonly Guid OtherParentUnitId = Guid.Parse("bbbbbbbb-2222-2222-2222-000000000ff2");
    private static readonly Guid CallerAgentId = Guid.Parse("cccccccc-3333-3333-3333-000000000ff1");
    private static readonly Guid SiblingAgentA = Guid.Parse("cccccccc-3333-3333-3333-000000000ff2");
    private static readonly Guid SiblingAgentB = Guid.Parse("cccccccc-3333-3333-3333-000000000ff3");

    [Fact]
    public async Task List_DefaultScope_ReturnsParentUnitMembersForAgentCaller()
    {
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .WithAgentMember(ParentUnitId, SiblingAgentA, displayName: "alpha")
            .WithAgentMember(ParentUnitId, SiblingAgentB, displayName: "beta")
            .Build();

        var json = await fixture.InvokeAsync(
            SvDirectorySkillRegistry.ListTool,
            JsonDocument.Parse("{}").RootElement);

        var entries = json.GetProperty("members").EnumerateArray().ToList();
        var uuids = entries.Select(e => e.GetProperty("uuid").GetString()!).ToList();
        // The default scope returns members of the agent caller's parent
        // unit — including the caller itself.
        uuids.ShouldContain(GuidFormatter.Format(CallerAgentId));
        uuids.ShouldContain(GuidFormatter.Format(SiblingAgentA));
        uuids.ShouldContain(GuidFormatter.Format(SiblingAgentB));
        json.GetProperty("total_count").GetInt32().ShouldBe(uuids.Count);
    }

    [Fact]
    public async Task List_SiblingsScope_ExcludesCaller()
    {
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .WithAgentMember(ParentUnitId, SiblingAgentA)
            .WithAgentMember(ParentUnitId, SiblingAgentB)
            .Build();

        var json = await fixture.InvokeAsync(
            SvDirectorySkillRegistry.ListTool,
            JsonDocument.Parse("""{ "scope": "siblings" }""").RootElement);

        var uuids = json.GetProperty("members").EnumerateArray()
            .Select(e => e.GetProperty("uuid").GetString()!)
            .ToList();
        uuids.ShouldNotContain(GuidFormatter.Format(CallerAgentId),
            "scope='siblings' must always exclude the caller itself.");
        uuids.ShouldContain(GuidFormatter.Format(SiblingAgentA));
        uuids.ShouldContain(GuidFormatter.Format(SiblingAgentB));
    }

    // ── #3069: the consolidated list folds humans into EVERY scope ──────────
    // The un-merged inconsistency the issue calls out: the scope path
    // (list / get_siblings) read only the agent member graph and EXCLUDED
    // humans, while list_members included them. After consolidation, list
    // includes human members on the scope paths too, subject to visibility
    // policy.

    private static readonly Guid HumanMemberId =
        Guid.Parse("00000000-aaaa-aaaa-aaaa-000000000abc");

    [Fact]
    public async Task List_UnitMembersScope_IncludesHumanMembers()
    {
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .WithAgentMember(ParentUnitId, SiblingAgentA)
            .WithHumanMember(ParentUnitId, HumanMemberId, roles: new[] { "owner" })
            .Build();

        var json = await fixture.InvokeAsync(
            SvDirectorySkillRegistry.ListTool,
            JsonDocument.Parse("{}").RootElement);

        var entries = json.GetProperty("members").EnumerateArray().ToList();
        var human = entries.SingleOrDefault(e =>
            e.GetProperty("kind").GetString() == SvDirectorySkillRegistry.KindHuman);
        human.ValueKind.ShouldNotBe(JsonValueKind.Undefined,
            "scope='unit_members' must fold in human members (the #3069 inconsistency fix).");
        human.GetProperty("uuid").GetString().ShouldBe(GuidFormatter.Format(HumanMemberId));
        human.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!)
            .ShouldBe(new[] { "owner" });
    }

    [Fact]
    public async Task List_SiblingsScope_IncludesHumanSiblings()
    {
        // A human teammate sharing the caller's parent unit is a sibling and
        // must surface on scope='siblings' — humans are never the caller, so
        // they are never the excluded self.
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .WithHumanMember(ParentUnitId, HumanMemberId)
            .Build();

        var json = await fixture.InvokeAsync(
            SvDirectorySkillRegistry.ListTool,
            JsonDocument.Parse("""{ "scope": "siblings" }""").RootElement);

        var uuids = json.GetProperty("members").EnumerateArray()
            .Select(e => e.GetProperty("uuid").GetString()!)
            .ToList();
        uuids.ShouldContain(GuidFormatter.Format(HumanMemberId));
    }

    [Fact]
    public async Task List_RoleFilter_NarrowsToEntriesWithMatchingRole()
    {
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .WithAgentMember(ParentUnitId, SiblingAgentA, roles: new[] { "owner" })
            .WithAgentMember(ParentUnitId, SiblingAgentB, roles: new[] { "reviewer" })
            .Build();

        var json = await fixture.InvokeAsync(
            SvDirectorySkillRegistry.ListTool,
            JsonDocument.Parse("""{ "role": "owner" }""").RootElement);

        var uuids = json.GetProperty("members").EnumerateArray()
            .Select(e => e.GetProperty("uuid").GetString()!)
            .ToList();
        uuids.ShouldBe(new[] { GuidFormatter.Format(SiblingAgentA) });
    }

    [Fact]
    public async Task List_RoleFilter_IsCaseInsensitiveAndSubstring()
    {
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .WithAgentMember(ParentUnitId, SiblingAgentA, roles: new[] { "Security_Lead" })
            .WithAgentMember(ParentUnitId, SiblingAgentB, roles: new[] { "ic" })
            .Build();

        var json = await fixture.InvokeAsync(
            SvDirectorySkillRegistry.ListTool,
            JsonDocument.Parse("""{ "role": "security" }""").RootElement);

        var uuids = json.GetProperty("members").EnumerateArray()
            .Select(e => e.GetProperty("uuid").GetString()!)
            .ToList();
        uuids.ShouldBe(new[] { GuidFormatter.Format(SiblingAgentA) });
    }

    // ── #3086: agent definition-level role surfaces on directory entries ──
    // The magazine-langgraph orchestrator resolves a teammate role to an
    // address via list_members → entry.roles, but every magazine member's
    // unit_memberships.roles is empty — the role lives on
    // agent_definitions.role. The directory must fold that definition-level
    // role into the entry's roles so role-based delegation resolves.

    [Fact]
    public async Task List_AgentWithEmptyMembershipRolesButDefinitionRole_SurfacesDefinitionRole()
    {
        // Membership roles are empty; the role is only on the agent
        // definition (role=staff-writer). list (#3069, explicit unit uuid)
        // must still surface staff-writer in the entry's roles array.
        var fixture = new Fixture()
            .WithAgentMember(ParentUnitId, SiblingAgentA, agentRole: "staff-writer")
            .Build();

        var json = await fixture.InvokeAsync(
            SvDirectorySkillRegistry.ListTool,
            JsonDocument.Parse($$"""{ "uuid": "{{GuidFormatter.Format(ParentUnitId)}}" }""").RootElement,
            callerId: ParentUnitId,
            callerKind: Address.UnitScheme);

        var entry = json.GetProperty("members").EnumerateArray()
            .Single(e => e.GetProperty("uuid").GetString() == GuidFormatter.Format(SiblingAgentA));
        var roles = entry.GetProperty("roles").EnumerateArray()
            .Select(r => r.GetString()!)
            .ToList();
        roles.ShouldBe(new[] { "staff-writer" });
    }

    [Fact]
    public async Task List_RoleFilter_MatchesAgentDefinitionRole()
    {
        // The role substring filter must match an agent whose role lives
        // only on its definition (empty membership roles) — this is the
        // exact resolve path the orchestrator's _role_address uses.
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .WithAgentMember(ParentUnitId, SiblingAgentA, agentRole: "fact-checker")
            .WithAgentMember(ParentUnitId, SiblingAgentB, agentRole: "copy-editor")
            .Build();

        var json = await fixture.InvokeAsync(
            SvDirectorySkillRegistry.ListTool,
            JsonDocument.Parse("""{ "role": "fact-checker" }""").RootElement);

        var uuids = json.GetProperty("members").EnumerateArray()
            .Select(e => e.GetProperty("uuid").GetString()!)
            .ToList();
        uuids.ShouldBe(new[] { GuidFormatter.Format(SiblingAgentA) });
    }

    [Fact]
    public async Task List_AgentWithMembershipRolesAndDefinitionRole_MergesBothDeduped()
    {
        // When membership roles ARE set, the existing roles are preserved in
        // order and the agent's definition role is appended (deduped
        // case-insensitively). Here "staff-writer" is also the definition
        // role, so it must not double up.
        var fixture = new Fixture()
            .WithAgentMember(
                ParentUnitId, SiblingAgentA,
                roles: new[] { "owner", "Staff-Writer" },
                agentRole: "managing-editor")
            .WithAgentMember(
                ParentUnitId, SiblingAgentB,
                roles: new[] { "reviewer" },
                agentRole: "reviewer")
            .Build();

        var json = await fixture.InvokeAsync(
            SvDirectorySkillRegistry.ListTool,
            JsonDocument.Parse($$"""{ "uuid": "{{GuidFormatter.Format(ParentUnitId)}}" }""").RootElement,
            callerId: ParentUnitId,
            callerKind: Address.UnitScheme);

        var entryA = json.GetProperty("members").EnumerateArray()
            .Single(e => e.GetProperty("uuid").GetString() == GuidFormatter.Format(SiblingAgentA));
        entryA.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!)
            .ShouldBe(new[] { "owner", "Staff-Writer", "managing-editor" });

        // SiblingAgentB's definition role equals its single membership role
        // (case-insensitive) — the append must dedupe it away.
        var entryB = json.GetProperty("members").EnumerateArray()
            .Single(e => e.GetProperty("uuid").GetString() == GuidFormatter.Format(SiblingAgentB));
        entryB.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!)
            .ShouldBe(new[] { "reviewer" });
    }

    [Fact]
    public async Task Lookup_AgentWithDefinitionRole_SurfacesRoleInEntry()
    {
        // The address-keyed lookup must fold the definition role in too, so
        // a runtime resolving an inbound sender sees its role.
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .WithAgentMember(ParentUnitId, SiblingAgentA, agentRole: "audience-editor")
            .Build();

        var addr = new Address(Address.AgentScheme, SiblingAgentA);
        var args = JsonDocument
            .Parse(JsonSerializer.Serialize(new { address = addr.ToString() }))
            .RootElement;

        var json = await fixture.InvokeAsync(SvDirectorySkillRegistry.LookupTool, args);

        json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!)
            .ShouldBe(new[] { "audience-editor" });
    }

    [Fact]
    public async Task Lookup_ByUuid_AgentWithDefinitionRole_SurfacesRoleInEntry()
    {
        // #3069: lookup-by-uuid (former get_member) shares BuildEntryAsync
        // with list, so the role fold must apply uniformly here too.
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .WithAgentMember(ParentUnitId, SiblingAgentA, agentRole: "production-editor")
            .Build();

        var json = await fixture.InvokeAsync(
            SvDirectorySkillRegistry.LookupTool,
            JsonDocument.Parse($$"""{ "uuid": "{{GuidFormatter.Format(SiblingAgentA)}}" }""").RootElement);

        json.GetProperty("roles").EnumerateArray().Select(r => r.GetString()!)
            .ShouldBe(new[] { "production-editor" });
    }

    [Fact]
    public async Task List_UnknownScope_Throws()
    {
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .Build();

        await Should.ThrowAsync<ArgumentException>(async () =>
            await fixture.InvokeAsync(
                SvDirectorySkillRegistry.ListTool,
                JsonDocument.Parse("""{ "scope": "not_a_scope" }""").RootElement));
    }

    [Fact]
    public async Task List_SelfMembersScope_FailsForAgentCaller()
    {
        // self_members is unit-only. An agent caller passing it must get
        // a typed argument error rather than silently fall back to
        // unit_members.
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .Build();

        await Should.ThrowAsync<ArgumentException>(async () =>
            await fixture.InvokeAsync(
                SvDirectorySkillRegistry.ListTool,
                JsonDocument.Parse("""{ "scope": "self_members" }""").RootElement));
    }

    [Fact]
    public async Task List_PaginationRespectsLimitAndOffset()
    {
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .WithAgentMember(ParentUnitId, SiblingAgentA)
            .WithAgentMember(ParentUnitId, SiblingAgentB)
            .Build();

        var json = await fixture.InvokeAsync(
            SvDirectorySkillRegistry.ListTool,
            JsonDocument.Parse("""{ "limit": 1, "offset": 1 }""").RootElement);

        json.GetProperty("members").GetArrayLength().ShouldBe(1);
        json.GetProperty("limit").GetInt32().ShouldBe(1);
        json.GetProperty("offset").GetInt32().ShouldBe(1);
        // total_count is the unfiltered length — 3 entries (caller + 2 siblings)
        json.GetProperty("total_count").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Lookup_AgentAddress_ReturnsEntryWithAddress()
    {
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .WithAgentMember(ParentUnitId, SiblingAgentA, displayName: "alpha")
            .Build();

        var addr = new Address(Address.AgentScheme, SiblingAgentA);
        var args = JsonDocument
            .Parse(JsonSerializer.Serialize(new { address = addr.ToString() }))
            .RootElement;

        var json = await fixture.InvokeAsync(SvDirectorySkillRegistry.LookupTool, args);

        json.GetProperty("address").GetString().ShouldBe(addr.ToString());
        json.GetProperty("uuid").GetString().ShouldBe(GuidFormatter.Format(SiblingAgentA));
        json.GetProperty("kind").GetString().ShouldBe(Address.AgentScheme);
        json.GetProperty("display_name").GetString().ShouldBe("alpha");
    }

    [Fact]
    public async Task Lookup_HumanAddress_ReturnsHumanEntry()
    {
        // Humans never appear in AgentDefinitions / UnitDefinitions, but
        // sv.directory.lookup must still resolve a human:... address into
        // a kind=human entry rather than throwing "no matching definition".
        var humanId = Guid.Parse("00000000-aaaa-aaaa-aaaa-000000000ff1");
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .Build();

        var addr = new Address(Address.HumanScheme, humanId);
        var args = JsonDocument
            .Parse(JsonSerializer.Serialize(new { address = addr.ToString() }))
            .RootElement;

        var json = await fixture.InvokeAsync(SvDirectorySkillRegistry.LookupTool, args);

        json.GetProperty("address").GetString().ShouldBe(addr.ToString());
        json.GetProperty("kind").GetString().ShouldBe(SvDirectorySkillRegistry.KindHuman);
    }

    [Fact]
    public async Task Lookup_InvalidAddress_Throws()
    {
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .Build();

        var args = JsonDocument
            .Parse("""{ "address": "not_an_address" }""")
            .RootElement;

        await Should.ThrowAsync<ArgumentException>(async () =>
            await fixture.InvokeAsync(SvDirectorySkillRegistry.LookupTool, args));
    }

    [Fact]
    public async Task Lookup_NeitherAddressNorUuid_Throws()
    {
        // #3069: lookup needs either an address or a uuid. With neither, it
        // throws a retry-guiding ArgumentException naming both inputs.
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .Build();

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await fixture.InvokeAsync(
                SvDirectorySkillRegistry.LookupTool,
                JsonDocument.Parse("{}").RootElement));
        ex.Message.ShouldContain("address");
        ex.Message.ShouldContain("uuid");
    }

    [Fact]
    public async Task Lookup_ByUuid_AgentReturnsEntryWithMaterialisedAddress()
    {
        // #3069: lookup-by-uuid (former get_member) resolves an agent and
        // stamps the canonical agent:<uuid> address on the entry, so the
        // result is feed-into-send ready just like the address path.
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .WithAgentMember(ParentUnitId, SiblingAgentA, displayName: "alpha")
            .Build();

        var json = await fixture.InvokeAsync(
            SvDirectorySkillRegistry.LookupTool,
            JsonDocument.Parse($$"""{ "uuid": "{{GuidFormatter.Format(SiblingAgentA)}}" }""").RootElement);

        json.GetProperty("uuid").GetString().ShouldBe(GuidFormatter.Format(SiblingAgentA));
        json.GetProperty("kind").GetString().ShouldBe(Address.AgentScheme);
        json.GetProperty("address").GetString()
            .ShouldBe(new Address(Address.AgentScheme, SiblingAgentA).ToString());
        json.GetProperty("display_name").GetString().ShouldBe("alpha");
    }

    [Fact]
    public async Task Lookup_ByUuid_UnknownUuid_Throws()
    {
        // Former get_member error contract: an unknown uuid throws
        // ArgumentException (surfaced by MCP as a typed tool error).
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .Build();

        var unknown = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await fixture.InvokeAsync(
                SvDirectorySkillRegistry.LookupTool,
                JsonDocument.Parse($$"""{ "uuid": "{{GuidFormatter.Format(unknown)}}" }""").RootElement));
        ex.Message.ShouldContain(GuidFormatter.Format(unknown));
    }

    [Fact]
    public async Task List_PolicyDenial_OmitsBlockedParentMembers()
    {
        // The enforcer denies reads on OtherParentUnitId; siblings under
        // that parent must NOT appear in the result while siblings under
        // the allowed parent still do.
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .WithParentMembership(CallerAgentId, OtherParentUnitId)
            .WithAgentMember(ParentUnitId, SiblingAgentA)
            .WithAgentMember(OtherParentUnitId, SiblingAgentB)
            .DenyDirectoryReadOn(OtherParentUnitId)
            .Build();

        var json = await fixture.InvokeAsync(
            SvDirectorySkillRegistry.ListTool,
            JsonDocument.Parse("""{ "scope": "siblings" }""").RootElement);

        var uuids = json.GetProperty("members").EnumerateArray()
            .Select(e => e.GetProperty("uuid").GetString()!)
            .ToList();
        uuids.ShouldContain(GuidFormatter.Format(SiblingAgentA));
        uuids.ShouldNotContain(GuidFormatter.Format(SiblingAgentB),
            "sibling under a parent the policy enforcer denied must not surface.");
    }

    // ── fixture ──────────────────────────────────────────────────────────

    // ---- #3069: consolidated sv.directory.list (scope / explicit uuid) ----

    [Fact]
    public async Task List_SiblingsScope_NoUuid_DefaultsToCaller()
    {
        // "my siblings" is the obvious intent of scope='siblings' with no
        // uuid — the tool resolves peers from the caller's own position in
        // the unit graph and excludes the caller itself. (#3069 folds the
        // former sv.directory.get_siblings no-uuid default into list.)
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .WithAgentMember(ParentUnitId, SiblingAgentA)
            .Build();

        var json = await fixture.InvokeAsync(
            SvDirectorySkillRegistry.ListTool,
            JsonDocument.Parse("""{ "scope": "siblings" }""").RootElement);

        var uuids = json.GetProperty("members").EnumerateArray()
            .Select(e => e.GetProperty("uuid").GetString()!)
            .ToList();
        uuids.ShouldContain(GuidFormatter.Format(SiblingAgentA));
        uuids.ShouldNotContain(GuidFormatter.Format(CallerAgentId),
            "scope='siblings' excludes self, and the default target is the caller.");
    }

    [Fact]
    public async Task List_ExplicitUuid_ListsThatUnitsMembers()
    {
        // #3069: an explicit uuid lists THAT unit's direct members (the
        // former sv.directory.list_members) and overrides scope — an agent
        // caller can list any unit it may read by uuid, not only its own
        // parent. The caller is NOT excluded on the explicit-uuid path.
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .WithAgentMember(ParentUnitId, SiblingAgentA)
            .WithAgentMember(ParentUnitId, SiblingAgentB)
            .Build();

        var json = await fixture.InvokeAsync(
            SvDirectorySkillRegistry.ListTool,
            JsonDocument.Parse($$"""{ "uuid": "{{GuidFormatter.Format(ParentUnitId)}}" }""").RootElement);

        var uuids = json.GetProperty("members").EnumerateArray()
            .Select(e => e.GetProperty("uuid").GetString()!)
            .ToList();
        uuids.ShouldContain(GuidFormatter.Format(SiblingAgentA));
        uuids.ShouldContain(GuidFormatter.Format(SiblingAgentB));
        uuids.ShouldContain(GuidFormatter.Format(CallerAgentId),
            "the explicit-uuid path lists the unit's full membership and does not exclude the caller.");
    }

    [Fact]
    public async Task List_MalformedUuid_ThrowsRetryGuidingError()
    {
        // A present-but-malformed uuid is a mistake to surface, not to paper
        // over by silently falling back to the scope path.
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .Build();

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await fixture.InvokeAsync(
                SvDirectorySkillRegistry.ListTool,
                JsonDocument.Parse("""{ "uuid": "not-a-guid" }""").RootElement));

        ex.Message.ShouldContain("uuid");
        ex.Message.ShouldContain("omit it");
    }

    [Fact]
    public async Task List_NoUuid_UnitCaller_DefaultsToOwnMembers()
    {
        // A unit calling list with no uuid (default scope='unit_members')
        // means "my members".
        var fixture = new Fixture()
            .WithAgentMember(ParentUnitId, SiblingAgentA)
            .WithAgentMember(ParentUnitId, SiblingAgentB)
            .Build();

        var json = await fixture.InvokeAsync(
            SvDirectorySkillRegistry.ListTool,
            JsonDocument.Parse("{}").RootElement,
            callerId: ParentUnitId,
            callerKind: Address.UnitScheme);

        var uuids = json.GetProperty("members").EnumerateArray()
            .Select(e => e.GetProperty("uuid").GetString()!)
            .ToList();
        uuids.ShouldContain(GuidFormatter.Format(SiblingAgentA));
        uuids.ShouldContain(GuidFormatter.Format(SiblingAgentB));
    }

    [Fact]
    public async Task List_NoUuid_AgentCaller_ReturnsParentUnitMembers()
    {
        // #3069 discoverability win: an agent calling list with no uuid is no
        // longer an error (the former sv.directory.list_members rejected it) —
        // the default scope='unit_members' returns the agent's parent unit
        // members directly, so the agent reaches its teammates in one call.
        var fixture = new Fixture()
            .WithParentMembership(CallerAgentId, ParentUnitId)
            .WithAgentMember(ParentUnitId, SiblingAgentA)
            .Build();

        var json = await fixture.InvokeAsync(
            SvDirectorySkillRegistry.ListTool,
            JsonDocument.Parse("{}").RootElement);

        var uuids = json.GetProperty("members").EnumerateArray()
            .Select(e => e.GetProperty("uuid").GetString()!)
            .ToList();
        uuids.ShouldContain(GuidFormatter.Format(SiblingAgentA));
        uuids.ShouldContain(GuidFormatter.Format(CallerAgentId),
            "scope='unit_members' returns the full parent-unit membership, including the caller.");
    }

    private sealed class Fixture
    {
        private readonly InMemoryUnitHumanMembershipStore _humanStore = new();
        private readonly InMemoryUnitMemberGraphStore _memberGraph = new();
        private readonly Dictionary<Guid, AgentDefinitionEntity> _agents = new();
        private readonly Dictionary<Guid, UnitDefinitionEntity> _units = new();
        private readonly Dictionary<(Guid Unit, Guid Agent), UnitMembership> _agentMemberships = new();
        private readonly HashSet<Guid> _callerParentUnits = new();
        private readonly HashSet<Guid> _deniedDirectoryReadUnits = new();

        public Fixture WithParentMembership(Guid agentId, Guid parentUnitId)
        {
            _callerParentUnits.Add(parentUnitId);
            // Caller-to-parent edges in the in-memory graph
            _memberGraph.SeedAgentMembers(parentUnitId, agentId);
            EnsureUnit(parentUnitId, $"unit-{parentUnitId:N}");
            EnsureAgent(agentId, $"agent-{agentId:N}");
            _agentMemberships[(parentUnitId, agentId)] = new UnitMembership(
                UnitId: parentUnitId,
                AgentId: agentId);
            return this;
        }

        public Fixture WithAgentMember(
            Guid unitId, Guid agentId,
            string? displayName = null,
            IReadOnlyList<string>? roles = null,
            IReadOnlyList<string>? expertise = null,
            string? agentRole = null)
        {
            _memberGraph.SeedAgentMembers(unitId, agentId);
            EnsureUnit(unitId, $"unit-{unitId:N}");
            EnsureAgent(agentId, displayName ?? $"agent-{agentId:N}", agentRole);
            _agentMemberships[(unitId, agentId)] = new UnitMembership(
                UnitId: unitId,
                AgentId: agentId,
                Roles: roles,
                Expertise: expertise);
            return this;
        }

        public Fixture WithHumanMember(
            Guid unitId, Guid humanId,
            IReadOnlyList<string>? roles = null,
            IReadOnlyList<string>? expertise = null)
        {
            EnsureUnit(unitId, $"unit-{unitId:N}");
            _humanStore.Seed(
                unitId, humanId,
                roles ?? Array.Empty<string>(),
                expertise ?? Array.Empty<string>(),
                Array.Empty<string>());
            return this;
        }

        public Fixture DenyDirectoryReadOn(Guid unitId)
        {
            _deniedDirectoryReadUnits.Add(unitId);
            return this;
        }

        private void EnsureAgent(Guid agentId, string displayName, string? role = null)
        {
            if (_agents.TryGetValue(agentId, out var existing))
            {
                // Allow a later WithAgentMember call to stamp the agent's
                // definition-level role even if an earlier WithParentMembership
                // seeded the bare agent first.
                if (!string.IsNullOrWhiteSpace(role))
                {
                    existing.Role = role;
                }
                return;
            }
            _agents[agentId] = new AgentDefinitionEntity
            {
                Id = agentId,
                TenantId = TenantId,
                DisplayName = displayName,
                Description = "test agent",
                Role = role,
            };
        }

        private void EnsureUnit(Guid unitId, string displayName)
        {
            if (_units.ContainsKey(unitId))
            {
                return;
            }
            _units[unitId] = new UnitDefinitionEntity
            {
                Id = unitId,
                TenantId = TenantId,
                DisplayName = displayName,
                Description = "test unit",
            };
        }

        public BuiltFixture Build()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            // SpringDbContext applies a tenant query filter against
            // ITenantContext.CurrentTenantId; the DI graph must supply
            // one or the DbContext's ctor fails. Register the same
            // tenant id the registry uses so the seeded rows stay
            // visible through the filter.
            services.AddSingleton<Cvoya.Spring.Core.Tenancy.ITenantContext>(
                new Cvoya.Spring.Dapr.Tenancy.StaticTenantContext(TenantId));

            var dbName = "sv-directory-list-lookup-" + Guid.NewGuid().ToString("N");
            services.AddDbContext<SpringDbContext>(opt => opt
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

            // Identity / display-name resolvers
            var identityResolver = Substitute.For<IHumanIdentityResolver>();
            identityResolver
                .GetDisplayNameAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(call => $"human-{call.ArgAt<Guid>(0):N}");
            services.AddScoped<IHumanIdentityResolver>(_ => identityResolver);

            var participantResolver = Substitute.For<IParticipantDisplayNameResolver>();
            participantResolver
                .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => new ValueTask<string>(call.ArgAt<string>(0)));
            services.AddScoped<IParticipantDisplayNameResolver>(_ => participantResolver);

            // Permissive enforcer by default; denies specific units when
            // requested. Returning the right PolicyDecision shape matches
            // what the registry consults.
            var enforcer = Substitute.For<IUnitPolicyEnforcer>();
            enforcer
                .EvaluateUnitDirectoryReadAsync(
                    Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var unitId = call.ArgAt<Guid>(1);
                    if (_deniedDirectoryReadUnits.Contains(unitId))
                    {
                        return PolicyDecision.Deny("test-deny", unitId.ToString("N"));
                    }
                    return PolicyDecision.Allowed;
                });
            services.AddScoped<IUnitPolicyEnforcer>(_ => enforcer);

            // Membership repo — both ListByUnit and ListByAgent must surface
            // the seeded rows; ListByAgent powers the caller's parent walk
            // for the agent caller path.
            var membershipRepo = Substitute.For<IUnitMembershipRepository>();
            membershipRepo
                .ListByUnitAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var unitId = call.ArgAt<Guid>(0);
                    return _agentMemberships
                        .Where(m => m.Key.Unit == unitId)
                        .Select(m => m.Value)
                        .ToList();
                });
            membershipRepo
                .ListByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var agentId = call.ArgAt<Guid>(0);
                    return _agentMemberships
                        .Where(m => m.Key.Agent == agentId)
                        .Select(m => m.Value)
                        .ToList();
                });
            services.AddScoped<IUnitMembershipRepository>(_ => membershipRepo);

            // Subunit repo — return empty for the caller's parent walk.
            var subunitRepo = Substitute.For<IUnitSubunitMembershipRepository>();
            subunitRepo
                .ListByChildAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<UnitSubunitMembership>());
            services.AddScoped<IUnitSubunitMembershipRepository>(_ => subunitRepo);

            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            // Seed the EF context (AgentDefinitions, UnitDefinitions, Tenants).
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
                db.Tenants.Add(new TenantRecordEntity
                {
                    Id = TenantId,
                    DisplayName = "test-tenant",
                });
                foreach (var agent in _agents.Values)
                {
                    db.AgentDefinitions.Add(agent);
                }
                foreach (var unit in _units.Values)
                {
                    db.UnitDefinitions.Add(unit);
                }
                db.SaveChanges();
            }

            var expertiseStore = Substitute.For<IExpertiseStore>();
            expertiseStore
                .GetDomainsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ExpertiseDomain>());

            var tenantContext = Substitute.For<ITenantContext>();
            tenantContext.CurrentTenantId.Returns(TenantId);

            // The registry calls live_status on agent / unit entries — for
            // these tests we don't exercise it, so the substitute returns
            // null proxies (calls throw, the registry tolerates and omits).
            var actorProxyFactory = Substitute.For<IActorProxyFactory>();

            // #3089: the single member-role seam, driven by the same
            // membership-role + agent-definition-role seeds the fixture
            // already builds so the seam-backed list paths resolve
            // identically to the EF join in production.
            var memberRoleDirectory = new InMemoryUnitMemberRoleDirectory();
            foreach (var ((unitId, agentId), membership) in _agentMemberships)
            {
                _agents.TryGetValue(agentId, out var agentDef);
                memberRoleDirectory.Seed(unitId, agentId, membership.Roles, agentDef?.Role);
            }

            // #3131: the registry resolves agent/unit kind through the shared
            // IDirectoryService.ResolveKindAsync seam. Wire a real
            // DirectoryService over the same scope factory so it reads the
            // identical in-memory SpringDbContext the fixture seeded — kind
            // resolution stays end-to-end correct after consolidation.
            var directoryService = new Cvoya.Spring.Dapr.Routing.DirectoryService(
                new Cvoya.Spring.Dapr.Routing.DirectoryCache(),
                scopeFactory,
                NullLoggerFactory.Instance);

            var registry = new SvDirectorySkillRegistry(
                scopeFactory,
                directoryService,
                _memberGraph,
                _humanStore,
                memberRoleDirectory,
                expertiseStore,
                actorProxyFactory,
                tenantContext,
                NullLoggerFactory.Instance);

            return new BuiltFixture(registry);
        }
    }

    private sealed class BuiltFixture
    {
        private readonly SvDirectorySkillRegistry _registry;
        public BuiltFixture(SvDirectorySkillRegistry registry) => _registry = registry;

        public Task<JsonElement> InvokeAsync(string toolName, JsonElement args) =>
            InvokeAsync(toolName, args, CallerAgentId, Address.AgentScheme);

        public Task<JsonElement> InvokeAsync(
            string toolName, JsonElement args, Guid callerId, string callerKind)
        {
            var ctx = new ToolCallContext(
                CallerId: GuidFormatter.Format(callerId),
                CallerKind: callerKind,
                ThreadId: Guid.NewGuid().ToString("N"));
            return _registry.InvokeAsync(toolName, args, ctx, TestContext.Current.CancellationToken);
        }
    }
}
