// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Execution;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Platform-level <see cref="ISkillRegistry"/> implementing the
/// <c>sv.*</c> directory tools (#2231). Lets the agent runtime navigate the
/// unit / agent composition graph at runtime — answering questions like
/// "who are the members of my unit", "who are my siblings", "who is my
/// parent" — without baking the directory into the system prompt.
/// </summary>
/// <remarks>
/// <para>
/// <b>Universal entry shape.</b> Every tool returns or includes one or more
/// entries with the same fields:
/// <c>{ uuid, kind, display_name, parent_uuids, description, expertise,
/// member_count, live_status? }</c>. <c>kind</c> is one of <c>"agent"</c>,
/// <c>"unit"</c>, <c>"human"</c>, or <c>"tenant"</c>. <c>parent_uuids</c> is
/// always a list (possibly empty for the tenant sentinel).
/// <c>member_count</c> is populated only for unit-kind entries;
/// <c>expertise</c> is empty for the tenant sentinel. <c>live_status</c>
/// (#2491) carries the advisory runtime snapshot from
/// <see cref="Cvoya.Spring.Core.Agents.AgentRuntimeStatusReport"/> on agent
/// and unit entries; it is omitted entirely from the wire shape for
/// <c>human</c> and <c>tenant</c> entries (humans have no runtime; the
/// tenant sentinel is non-addressable). Per-subject failures on the
/// actor-proxy read are tolerated — the affected entry simply omits the
/// field while siblings keep theirs.
/// </para>
/// <para>
/// <b>Tenant sentinel.</b> The tenant is rendered as an entry with
/// <c>kind == "tenant"</c> and <c>uuid == ITenantContext.CurrentTenantId</c>.
/// The sentinel is structurally load-bearing, not cosmetic: without it the
/// caller cannot distinguish "agent A is in unit U1 only" (parent_uuids:
/// [U1]) from "agent A is in unit U1 AND directly at tenant root"
/// (parent_uuids: [U1, &lt;tenant&gt;]). Tool descriptions explicitly warn
/// the model that the tenant entry is non-addressable — work assignment,
/// messaging, and delegation against it are invalid; it serves only as a
/// navigation marker for "top of the hierarchy".
/// </para>
/// <para>
/// <b>UUID-only public surface.</b> The wire shape never accepts or returns
/// platform-internal addresses (<c>scheme://path</c>). Every reference
/// crosses the tool boundary as a uuid; <c>kind</c> on the response disc-
/// riminates so callers can construct addresses locally if they ever need
/// to. The agent runtime knows its own uuid via <c>SPRING_AGENT_ID</c> and
/// can also read it via <c>sv.directory.get_self()</c>.
/// </para>
/// <para>
/// <b>Authz.</b> Every tool gates the read through
/// <see cref="IUnitPolicyEnforcer.EvaluateUnitDirectoryReadAsync"/> for the
/// target unit. Tenant isolation is enforced one layer down — every read
/// the registry issues goes through tenant-scoped EF queries
/// (<see cref="IUnitMemberGraphStore"/>, the membership repositories), so a
/// caller in tenant A cannot observe tenant B regardless of the verdict
/// returned by the enforcer.
/// </para>
/// <para>
/// <b>Per-call cost.</b> Single-target tools (<c>get_self</c>,
/// <c>get_member</c>) issue O(1) reads per dependency. List tools
/// (<c>list_members</c>, <c>get_siblings</c>, <c>get_parents</c>) populate
/// the rich entry fields per element, which means N reads against
/// <see cref="IExpertiseStore"/> (which round-trips to the actor today) and
/// <see cref="IParticipantDisplayNameResolver"/>. For v0.1 unit sizes this
/// is acceptable; if list latency becomes a hotspot, batch-read methods on
/// the underlying stores are the natural follow-up — the wire shape does
/// not change.
/// </para>
/// </remarks>
public sealed class SvDirectorySkillRegistry : ISkillRegistry
{
    /// <summary>Tool name for <c>sv.directory.get_self</c>.</summary>
    public const string GetSelfTool = "sv.directory.get_self";

    /// <summary>Tool name for <c>sv.directory.get_member</c>.</summary>
    public const string GetMemberTool = "sv.directory.get_member";

    /// <summary>Tool name for <c>sv.directory.list_members</c>.</summary>
    public const string ListMembersTool = "sv.directory.list_members";

    /// <summary>Tool name for <c>sv.directory.get_siblings</c>.</summary>
    public const string GetSiblingsTool = "sv.directory.get_siblings";

    /// <summary>Tool name for <c>sv.directory.get_parents</c>.</summary>
    public const string GetParentsTool = "sv.directory.get_parents";

    /// <summary>Tool name for <c>sv.directory.get_status</c> (#2491).</summary>
    public const string GetStatusTool = "sv.directory.get_status";

    /// <summary>
    /// Tool name for <c>sv.directory.list</c> — ADR-0056 §8 fundamental-core
    /// directory tool. Convenience over <see cref="ListMembersTool"/> /
    /// <see cref="GetSiblingsTool"/>: accepts a <c>scope</c> argument that
    /// resolves against the calling agent / unit's position in the unit
    /// graph (members of the caller's parent unit, siblings, or the
    /// caller's own members), plus optional <c>role</c> / <c>expertise</c>
    /// filters that narrow the result.
    /// </summary>
    public const string ListTool = "sv.directory.list";

    /// <summary>
    /// Tool name for <c>sv.directory.lookup</c> — ADR-0056 §8 fundamental-core
    /// directory tool. Address-keyed single-entry lookup. Mirrors
    /// <see cref="GetMemberTool"/> but accepts a canonical address string
    /// (<c>scheme:&lt;hex&gt;</c>) rather than a bare uuid so a runtime can
    /// feed back the sender of an inbound message without re-parsing it
    /// out of its address.
    /// </summary>
    public const string LookupTool = "sv.directory.lookup";

    /// <summary>Default page size for list tools, mirroring DirectorySearchResponse.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Maximum allowed page size, mirroring DirectorySearchResponse.</summary>
    public const int MaxLimit = 200;

    private const string KindAgent = Address.AgentScheme;
    private const string KindUnit = Address.UnitScheme;
    private const string KindTenant = "tenant";

    /// <summary>
    /// Wire-format <c>kind</c> value for human team members surfaced via
    /// <c>sv.directory.list_members</c> (ADR-0044 § 5, reshaped by ADR-0046 §9). One
    /// entry per <c>unit_memberships_humans</c> row; entries carry a
    /// multi-valued <c>roles</c> array (replaces the per-row
    /// <c>team_role: string</c> field from ADR-0044).
    /// </summary>
    public const string KindHuman = "human";

    private static readonly JsonElement EmptyObjectSchema = ParseSchema("""
        { "type": "object", "additionalProperties": false, "properties": {} }
        """);
    private static readonly JsonElement UuidArgSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["uuid"],
          "properties": {
            "uuid": {
              "type": "string",
              "description": "Stable Guid identifier (no-dash 32-char hex form, also accepts standard dashed UUID form)."
            }
          }
        }
        """);
    private static readonly JsonElement UuidPagedArgSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "uuid": { "type": "string", "description": "Stable Guid identifier (no-dash 32-char hex, also accepts dashed UUID). Optional — when omitted, defaults to you, the calling agent or unit: sv.directory.get_siblings returns your own siblings; sv.directory.list_members returns your own members (valid only when you are a unit, since agents have no members)." },
            "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 50 },
            "offset": { "type": "integer", "minimum": 0, "default": 0 }
          }
        }
        """);

    /// <summary>
    /// Input schema for <c>sv.directory.list</c> — ADR-0056 §8 fundamental
    /// core. All arguments are optional. <c>scope</c> defaults to
    /// <c>unit_members</c>; <c>role</c> / <c>expertise</c> narrow the
    /// resulting set by case-insensitive substring match against the
    /// per-entry roles list and expertise-domain names; <c>limit</c> /
    /// <c>offset</c> mirror the existing list-tool pagination contract.
    /// </summary>
    private static readonly JsonElement ListArgSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "scope": {
              "type": "string",
              "enum": ["unit_members", "siblings", "self_members"],
              "description": "Which member set to resolve. 'unit_members' (default) returns the members of the caller's parent unit(s) when the caller is an agent, or the caller's own members when the caller is a unit. 'siblings' returns peers — entries sharing a parent with the caller, excluding the caller itself. 'self_members' is the unit-only equivalent of 'unit_members' that always returns the caller's own members (errors for agent callers).",
              "default": "unit_members"
            },
            "role": {
              "type": "string",
              "description": "Optional case-insensitive substring filter on each entry's roles list. Entries with no matching role are excluded."
            },
            "expertise": {
              "type": "string",
              "description": "Optional case-insensitive substring filter on each entry's expertise-domain names. Entries with no matching domain are excluded."
            },
            "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 50 },
            "offset": { "type": "integer", "minimum": 0, "default": 0 }
          }
        }
        """);

    /// <summary>
    /// Input schema for <c>sv.directory.lookup</c>. The required
    /// <c>address</c> string is the canonical Spring Voyage address form
    /// (<c>scheme:&lt;32-hex&gt;</c>) — for example the
    /// <c>From</c> field of the inbound message the runtime is
    /// responding to.
    /// </summary>
    private static readonly JsonElement LookupArgSchema = ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["address"],
          "properties": {
            "address": {
              "type": "string",
              "description": "Canonical Spring Voyage address (scheme:32-hex-no-dash) — agent:..., unit:..., or human:... — to resolve."
            }
          }
        }
        """);

    private const string TenantSentinelWarning =
        "If a returned entry has kind='tenant', it represents the top of the unit hierarchy " +
        "and is NOT addressable — do not assign work to it, send messages to it, or delegate " +
        "to it. The tenant entry exists only as a navigation marker so callers can distinguish, " +
        "for example, 'agent A is only in unit U1' from 'agent A is in unit U1 AND directly at " +
        "the top level'.";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUnitMemberGraphStore _memberGraphStore;
    private readonly IUnitHumanMembershipStore _humanMembershipStore;
    private readonly IUnitMemberRoleDirectory _memberRoleDirectory;
    private readonly IExpertiseStore _expertiseStore;
    private readonly IActorProxyFactory _actorProxyFactory;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger _logger;

    private readonly IReadOnlyList<ToolDefinition> _tools;

    /// <summary>Builds the registry with its singleton dependencies.</summary>
    /// <param name="scopeFactory">Scope factory for the per-call scoped reads (definitions, repositories).</param>
    /// <param name="memberGraphStore">Read seam over <c>unit_memberships</c> / <c>unit_subunit_memberships</c>.</param>
    /// <param name="humanMembershipStore">Read seam over <c>unit_memberships_humans</c>.</param>
    /// <param name="memberRoleDirectory">
    /// Single DB-backed seam (#3089) that resolves agent-member effective
    /// roles — <c>unit_memberships.roles ∪ agent_definitions.role</c>,
    /// deduped — from one join. Replaces the divergent per-membership role
    /// passes the list tools used to run independently.
    /// </param>
    /// <param name="expertiseStore">Read seam for the actor expertise surfaced on every entry.</param>
    /// <param name="actorProxyFactory">
    /// Factory for building <see cref="IAgentActor"/> / <see cref="IUnitActor"/>
    /// proxies (#2491). The registry calls <c>GetRuntimeStatusAsync</c> on the
    /// proxy to populate the <c>live_status</c> field on agent / unit entries.
    /// Per-subject failures are tolerated — see <see cref="ResolveLiveStatusAsync"/>.
    /// </param>
    /// <param name="tenantContext">Current-tenant resolver (the tenant sentinel anchors at <see cref="ITenantContext.CurrentTenantId"/>).</param>
    /// <param name="loggerFactory">Logger factory for diagnostic logging.</param>
    public SvDirectorySkillRegistry(
        IServiceScopeFactory scopeFactory,
        IUnitMemberGraphStore memberGraphStore,
        IUnitHumanMembershipStore humanMembershipStore,
        IUnitMemberRoleDirectory memberRoleDirectory,
        IExpertiseStore expertiseStore,
        IActorProxyFactory actorProxyFactory,
        ITenantContext tenantContext,
        ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _memberGraphStore = memberGraphStore;
        _humanMembershipStore = humanMembershipStore;
        _memberRoleDirectory = memberRoleDirectory;
        _expertiseStore = expertiseStore;
        _actorProxyFactory = actorProxyFactory;
        _tenantContext = tenantContext;
        _logger = loggerFactory.CreateLogger<SvDirectorySkillRegistry>();

        _tools = new[]
        {
            new ToolDefinition(
                GetSelfTool,
                "Returns metadata for the calling agent or unit (the entity whose runtime is " +
                "executing this tool call). Output is a single entry: " +
                "{ uuid, kind, display_name, parent_uuids, description, expertise, member_count, live_status? }. " +
                "Use this as the bootstrap when navigating the unit hierarchy — the entry's " +
                "parent_uuids are the starting point for sv.directory.get_parents / sv.directory.list_members. " +
                "live_status is an advisory snapshot of in-flight work — see sv.directory.get_status for " +
                "details; the field is omitted on entries whose kind doesn't carry runtime state.",
                EmptyObjectSchema,
                ToolCategories.Directory),
            new ToolDefinition(
                GetMemberTool,
                "Returns metadata for a single agent or unit identified by uuid. Output shape " +
                "matches sv.directory.get_self. " + TenantSentinelWarning,
                UuidArgSchema,
                ToolCategories.Directory),
            new ToolDefinition(
                ListMembersTool,
                "Returns the direct members of the unit identified by uuid — agents, sub-units, " +
                "AND human members — as a flat list mixing agent-kind, unit-kind, and human-kind " +
                "entries (filter by entry.kind on the client side if you only want one). Every " +
                "entry carries a SENDABLE address, so this is the tool to use to look up a " +
                "teammate's address — including a human member — and feed it straight into " +
                "sv.messaging.send, without asking the hub. " +
                "Every entry also carries an optional multi-valued roles array (free-form labels " +
                "like owner, reviewer, security_lead) and an expertise list. Human entries also " +
                "surface their notifications subscription list when present. One row per (unit, " +
                "human) pair — a human filling multiple team roles surfaces as a single entry " +
                "whose roles array carries every role. Sub-unit members are NOT recursively " +
                "expanded — call sv.directory.list_members again on a sub-unit's uuid to walk " +
                "further. Omit uuid to list your own members — valid only when you are a unit " +
                "(agents have no members and get a retry-guiding error). Pagination via limit " +
                "(default 50, max 200) and offset; total_count carries the unfiltered total. " +
                "Agent and unit entries additionally carry an advisory live_status object — see " +
                "sv.directory.get_status; the field is omitted entirely on human entries.",
                UuidPagedArgSchema,
                ToolCategories.Directory),
            new ToolDefinition(
                GetSiblingsTool,
                "Returns entities that share at least one parent with the entity identified by " +
                "uuid. Self is excluded. Omit uuid to default to yourself — your own siblings. " +
                "Each returned entry carries its own parent_uuids so the caller can filter by " +
                "overlap with the target's parents to scope to a specific context (e.g. 'siblings " +
                "under the unit that just messaged me'). Pagination via limit (default 50, max 200) " +
                "and offset.",
                UuidPagedArgSchema,
                ToolCategories.Directory),
            new ToolDefinition(
                GetParentsTool,
                "Returns the parents of the entity identified by uuid. For top-level units the " +
                "list contains the tenant sentinel entry (kind='tenant'). " + TenantSentinelWarning,
                UuidArgSchema,
                ToolCategories.Directory),
            new ToolDefinition(
                GetStatusTool,
                "Returns the advisory runtime-status snapshot for a single agent or unit " +
                "identified by uuid. Output shape: { uuid, kind, display_name, live_status? }. " +
                "live_status is { in_flight, queued, channels, observed_at } where in_flight is " +
                "the count of conversations currently being dispatched, queued is messages waiting " +
                "behind in-flight heads (agents only; units' lean dispatch has no queue), and " +
                "channels is the total per-conversation channels tracked. The field is omitted for " +
                "kind='human' (humans have no runtime). Snapshots are advisory — they reflect " +
                "the state at the moment of the call and may be stale by the time you act on " +
                "them; the actor mailbox is the ordering authority. " + TenantSentinelWarning,
                UuidArgSchema,
                ToolCategories.Directory),
            new ToolDefinition(
                ListTool,
                "Resolve members of the caller's unit, the caller's siblings, or peers " +
                "matching a role / expertise filter — ADR-0056 §8 fundamental-core surface. " +
                "Defaults to scope='unit_members' (the members of the caller's parent unit " +
                "when the caller is an agent; the caller's own members when the caller is a " +
                "unit). Each entry carries enough to act on: address, kind, display_name, " +
                "roles, expertise, parent_uuids, member_count (for unit entries), and an " +
                "advisory live_status (agent / unit entries only). Use the returned address " +
                "with sv.messaging.send to reach the entry. Pagination via limit (default 50, " +
                "max 200) and offset; total_count carries the unfiltered total.",
                ListArgSchema,
                ToolCategories.Directory),
            new ToolDefinition(
                LookupTool,
                "Resolve a known canonical address (scheme:32-hex) to a single directory " +
                "entry — ADR-0056 §8 fundamental-core surface. Output shape matches one " +
                "sv.directory.list entry: { address, uuid, kind, display_name, parent_uuids, " +
                "description, expertise, roles, member_count?, live_status? }. Use this when " +
                "you have an address (typically the sender of the inbound message) and need " +
                "the entry's role / expertise / status before deciding how to respond. " +
                TenantSentinelWarning,
                LookupArgSchema,
                ToolCategories.Directory),
        };
    }

    /// <inheritdoc />
    public string Name => "sv";

    /// <inheritdoc />
    public IReadOnlyList<ToolDefinition> GetToolDefinitions() => _tools;

    /// <inheritdoc />
    public Task<JsonElement> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default) =>
        throw new SpringException(
            $"Tool '{toolName}' on the {Name} registry requires caller context. " +
            "It is reachable only through the caller-aware ISkillRegistry.InvokeAsync overload " +
            "(invoked by the MCP server with the active session's identity). Direct callers " +
            "must use the rich overload.");

    /// <inheritdoc />
    public async Task<JsonElement> InvokeAsync(
        string toolName,
        JsonElement arguments,
        ToolCallContext context,
        CancellationToken cancellationToken = default)
    {
        switch (toolName)
        {
            case GetSelfTool:
                return await GetSelfAsync(context, cancellationToken);
            case GetMemberTool:
                return await GetMemberAsync(arguments, context, cancellationToken);
            case ListMembersTool:
                return await ListMembersAsync(arguments, context, cancellationToken);
            case GetSiblingsTool:
                return await GetSiblingsAsync(arguments, context, cancellationToken);
            case GetParentsTool:
                return await GetParentsAsync(arguments, context, cancellationToken);
            case GetStatusTool:
                return await GetStatusAsync(arguments, context, cancellationToken);
            case ListTool:
                return await ListAsync(arguments, context, cancellationToken);
            case LookupTool:
                return await LookupAsync(arguments, context, cancellationToken);
            default:
                throw new SkillNotFoundException(toolName);
        }
    }

    /// <summary>
    /// Implements <c>sv.directory.list</c> (ADR-0056 §8). Resolves the
    /// active scope against the caller's position in the unit graph,
    /// builds <see cref="DirectoryEntry"/> projections for each member,
    /// and applies optional <c>role</c> / <c>expertise</c> substring
    /// filters before paginating. Authz is delegated to the same
    /// per-target-unit enforcer the other list tools use — the caller
    /// only sees members of units they may read.
    /// </summary>
    private async Task<JsonElement> ListAsync(
        JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var scope = ParseListScope(args);
        var roleFilter = TryGetStringArgument(args, "role");
        var expertiseFilter = TryGetStringArgument(args, "expertise");
        var (limit, offset) = ParsePaging(args);

        var (callerUuid, callerKind) = ParseCaller(context);
        var sourceUnitIds = await ResolveScopeUnitIdsAsync(callerUuid, callerKind, scope, context, ct);
        var addresses = await CollectMembersAsync(sourceUnitIds, scope, callerUuid, ct);

        // #3089: the per-member effective roles — which is what the role
        // filter is meant to match against — come from the single member-
        // role seam (membership roles ∪ agent_definitions.role, deduped),
        // computed once per source unit. A single agent appearing under
        // more than one source unit takes the union across those units.
        var rolesByAgentId = await ResolveAgentRolesAcrossUnitsAsync(sourceUnitIds, ct);

        // Materialise every entry first so the filters can match on the
        // rich shape (roles, expertise). Unit sizes in v0.1 keep this
        // cheap; the same per-entry build cost as the existing
        // sv.directory.list_members tool.
        var built = new List<DirectoryEntry>(addresses.Count);
        foreach (var address in addresses)
        {
            var entry = await BuildEntryAsync(address.Path, address.Scheme, ct);
            if (string.Equals(address.Scheme, KindAgent, StringComparison.Ordinal)
                && GuidFormatter.TryParse(address.Path, out var agentGuid)
                && rolesByAgentId.TryGetValue(agentGuid, out var roles)
                && roles.Count > 0)
            {
                entry = entry with { Roles = roles };
            }
            built.Add(entry);
        }

        var filtered = built
            .Where(e => MatchesRole(e, roleFilter))
            .Where(e => MatchesExpertise(e, expertiseFilter))
            .ToList();

        var page = filtered.Skip(offset).Take(limit).ToList();
        return SerializePagedList("members", page, filtered.Count, limit, offset);
    }

    /// <summary>
    /// Resolves the source units the scope draws members from. For
    /// <see cref="ListScope.SelfMembers"/> and the unit-caller default
    /// this is the caller's own unit; for the agent-caller default and
    /// <see cref="ListScope.Siblings"/> it walks the caller's parents
    /// (filtered through the directory-read enforcer). The tenant
    /// sentinel is excluded — it has no members.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ResolveScopeUnitIdsAsync(
        string callerUuid, string callerKind, ListScope scope,
        ToolCallContext context, CancellationToken ct)
    {
        if (scope == ListScope.SelfMembers)
        {
            if (!string.Equals(callerKind, KindUnit, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "scope='self_members' is only valid when the caller is a unit. " +
                    "Use 'unit_members' for agent callers.");
            }
            if (!GuidFormatter.TryParse(callerUuid, out var selfGuid))
            {
                return Array.Empty<Guid>();
            }
            await EnsureDirectoryReadAllowedAsync(context, callerUuid, ct);
            return new[] { selfGuid };
        }

        if (scope == ListScope.UnitMembers
            && string.Equals(callerKind, KindUnit, StringComparison.Ordinal))
        {
            if (!GuidFormatter.TryParse(callerUuid, out var selfGuid))
            {
                return Array.Empty<Guid>();
            }
            await EnsureDirectoryReadAllowedAsync(context, callerUuid, ct);
            return new[] { selfGuid };
        }

        var parentUuids = await ResolveParentUuidsAsync(callerUuid, callerKind, ct);
        var tenantUuid = GuidFormatter.Format(_tenantContext.CurrentTenantId);
        var result = new List<Guid>();
        foreach (var parentUuid in parentUuids)
        {
            if (string.Equals(parentUuid, tenantUuid, StringComparison.Ordinal))
            {
                continue;
            }
            if (!GuidFormatter.TryParse(parentUuid, out var parentGuid))
            {
                continue;
            }
            if (!await IsDirectoryReadAllowedAsync(context, parentGuid, ct))
            {
                continue;
            }
            result.Add(parentGuid);
        }
        return result;
    }

    /// <summary>
    /// Collects the member address set from every source unit, deduping
    /// across overlapping parents and excluding the caller for
    /// <see cref="ListScope.Siblings"/>.
    /// </summary>
    private async Task<IReadOnlyList<Address>> CollectMembersAsync(
        IReadOnlyList<Guid> sourceUnitIds, ListScope scope, string callerUuid, CancellationToken ct)
    {
        var resultByPath = new Dictionary<string, Address>(StringComparer.Ordinal);
        foreach (var unitId in sourceUnitIds)
        {
            var members = await _memberGraphStore.GetMembersAsync(unitId, ct);
            foreach (var member in members)
            {
                if (scope == ListScope.Siblings
                    && string.Equals(member.Path, callerUuid, StringComparison.Ordinal))
                {
                    continue;
                }
                resultByPath[member.Path] = member;
            }
        }
        return resultByPath.Values.ToList();
    }

    /// <summary>
    /// Resolves the effective roles for every agent member across the
    /// supplied source units via the single <see cref="IUnitMemberRoleDirectory"/>
    /// seam (#3089). When a single agent appears in more than one source
    /// unit, the per-unit effective-role lists are unioned (case-insensitive
    /// dedupe, stable order) — the filter then matches across the agent's
    /// combined membership set, which is what an operator would expect
    /// "find members with role X" to mean.
    /// </summary>
    private async Task<Dictionary<Guid, IReadOnlyList<string>>> ResolveAgentRolesAcrossUnitsAsync(
        IReadOnlyList<Guid> sourceUnitIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, IReadOnlyList<string>>();
        if (sourceUnitIds.Count == 0)
        {
            return result;
        }

        var accumulator = new Dictionary<Guid, List<string>>();
        foreach (var unitId in sourceUnitIds)
        {
            var rolesByAgent = await _memberRoleDirectory.GetAgentMemberRolesAsync(unitId, ct);
            foreach (var (agentId, roles) in rolesByAgent)
            {
                if (!accumulator.TryGetValue(agentId, out var bag))
                {
                    bag = new List<string>();
                    accumulator[agentId] = bag;
                }
                foreach (var role in roles)
                {
                    if (!bag.Contains(role, StringComparer.OrdinalIgnoreCase))
                    {
                        bag.Add(role);
                    }
                }
            }
        }
        foreach (var (agentId, roles) in accumulator)
        {
            result[agentId] = roles;
        }
        return result;
    }

    /// <summary>
    /// Implements <c>sv.directory.lookup</c> (ADR-0056 §8). Accepts a
    /// canonical address string, parses out the scheme + uuid, and
    /// delegates to the same per-kind build path
    /// <see cref="GetMemberAsync"/> uses — the wire shape on success is
    /// one <see cref="DirectoryEntry"/> with the inbound address stamped
    /// alongside the existing uuid field for caller convenience.
    /// </summary>
    private async Task<JsonElement> LookupAsync(
        JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var addressValue = RequireStringArgument(args, "address");
        if (!Address.TryParse(addressValue, out var target) || target is null)
        {
            throw new ArgumentException(
                $"Argument 'address' value '{addressValue}' is not a valid Spring Voyage address.");
        }

        var uuid = target.Path;
        var (resolvedKind, displayNameOverride) = await ResolveLookupKindAsync(uuid, target.Scheme, ct);

        if (string.Equals(resolvedKind, KindUnit, StringComparison.Ordinal))
        {
            await EnsureDirectoryReadAllowedAsync(context, uuid, ct);
        }

        var entry = await BuildEntryAsync(uuid, resolvedKind, ct, displayNameOverride);
        return SerializeLookupEntry(target, entry);
    }

    private async Task<(string Kind, string? DisplayName)> ResolveLookupKindAsync(
        string uuid, string scheme, CancellationToken ct)
    {
        // Humans never appear in AgentDefinitions / UnitDefinitions, so
        // ResolveKindAsync would throw for a human:... address. Detect
        // the human scheme here so the lookup succeeds with the entry
        // shape the caller expects.
        if (string.Equals(scheme, KindHuman, StringComparison.Ordinal))
        {
            return (KindHuman, null);
        }

        return await ResolveKindAsync(uuid, ct);
    }

    private static ListScope ParseListScope(JsonElement args)
    {
        var raw = TryGetStringArgument(args, "scope");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ListScope.UnitMembers;
        }
        return raw switch
        {
            "unit_members" => ListScope.UnitMembers,
            "siblings" => ListScope.Siblings,
            "self_members" => ListScope.SelfMembers,
            _ => throw new ArgumentException(
                $"Argument 'scope' value '{raw}' is not recognised. " +
                "Use 'unit_members', 'siblings', or 'self_members'."),
        };
    }

    private static bool MatchesRole(DirectoryEntry entry, string? roleFilter)
    {
        if (string.IsNullOrWhiteSpace(roleFilter))
        {
            return true;
        }
        // #3089 / #3086: filter against the entry's effective roles
        // (membership roles ∪ the agent's definition-level role, resolved
        // once by the member-role seam) so `sv.directory.list role=<x>`
        // matches an agent whose role lives only on its definition.
        var roles = EntryRoles(entry);
        if (roles.Count == 0)
        {
            return false;
        }
        foreach (var role in roles)
        {
            if (!string.IsNullOrEmpty(role)
                && role.Contains(roleFilter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MatchesExpertise(DirectoryEntry entry, string? expertiseFilter)
    {
        if (string.IsNullOrWhiteSpace(expertiseFilter))
        {
            return true;
        }
        foreach (var domain in entry.Expertise)
        {
            if (!string.IsNullOrEmpty(domain.Name)
                && domain.Name.Contains(expertiseFilter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string RequireStringArgument(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var prop) ||
            prop.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Missing required argument '{name}'.");
        }
        var raw = prop.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException($"Argument '{name}' must be a non-empty string.");
        }
        return raw;
    }

    private static string? TryGetStringArgument(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var prop) ||
            prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var raw = prop.GetString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    /// <summary>
    /// Renders one <see cref="DirectoryEntry"/> as the
    /// <c>sv.directory.lookup</c> wire shape — the existing entry shape
    /// plus a top-level <c>address</c> field carrying the canonical
    /// address string the caller passed in, so a runtime that wants to
    /// feed the address back into <c>sv.messaging.send</c> doesn't have
    /// to reconstruct it from <c>kind</c> + <c>uuid</c>.
    /// </summary>
    private static JsonElement SerializeLookupEntry(Address address, DirectoryEntry entry)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("address", address.ToString());
            writer.WriteString("uuid", entry.Uuid);
            writer.WriteString("kind", entry.Kind);
            writer.WriteString("display_name", entry.DisplayName);
            writer.WritePropertyName("parent_uuids");
            writer.WriteStartArray();
            foreach (var parent in entry.ParentUuids)
            {
                writer.WriteStringValue(parent);
            }
            writer.WriteEndArray();
            writer.WriteString("description", entry.Description);
            writer.WritePropertyName("expertise");
            writer.WriteStartArray();
            foreach (var domain in entry.Expertise)
            {
                writer.WriteStartObject();
                writer.WriteString("name", domain.Name);
                writer.WriteString("description", domain.Description ?? string.Empty);
                if (domain.Level is { } level)
                {
                    writer.WriteString("level", level.ToString().ToLowerInvariant());
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (entry.MemberCount is { } count)
            {
                writer.WriteNumber("member_count", count);
            }
            else
            {
                writer.WriteNull("member_count");
            }
            if (entry.LiveStatus is { } report)
            {
                writer.WritePropertyName("live_status");
                WriteLiveStatus(writer, report);
            }
            // #3089: emit the entry's already-resolved effective roles,
            // same as WriteEntry. For the address-keyed lookup the agent's
            // definition-level role was folded in by BuildEntryAsync.
            writer.WritePropertyName("roles");
            writer.WriteStartArray();
            foreach (var role in EntryRoles(entry))
            {
                writer.WriteStringValue(role);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// Scope values accepted by <c>sv.directory.list</c> (ADR-0056 §8).
    /// </summary>
    private enum ListScope
    {
        /// <summary>Members of the caller's parent unit(s); for unit callers, the caller's own members.</summary>
        UnitMembers,

        /// <summary>Co-members of the caller's parent unit(s), excluding the caller.</summary>
        Siblings,

        /// <summary>Members of the caller itself (unit callers only).</summary>
        SelfMembers,
    }

    private async Task<JsonElement> GetSelfAsync(ToolCallContext context, CancellationToken ct)
    {
        var (uuid, kind) = ParseCaller(context);
        var entry = await BuildEntryAsync(uuid, kind, ct);
        return SerializeEntry(entry);
    }

    private async Task<JsonElement> GetMemberAsync(JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var targetUuid = RequireUuidArg(args, "uuid");
        var (kind, displayNameOverride) = await ResolveKindAsync(targetUuid, ct);
        if (kind == KindUnit)
        {
            await EnsureDirectoryReadAllowedAsync(context, targetUuid, ct);
        }

        var entry = await BuildEntryAsync(targetUuid, kind, ct, displayNameOverride);
        return SerializeEntry(entry);
    }

    /// <summary>
    /// Implements <c>sv.directory.get_status</c> (#2491). Returns a slim projection
    /// — <c>{ uuid, kind, display_name, live_status? }</c> — for one
    /// subject identified by uuid. Same error shape as <c>sv.directory.get_member</c>:
    /// an unknown / unparseable uuid throws <see cref="ArgumentException"/>,
    /// which the MCP server surfaces as a typed tool error.
    /// </summary>
    private async Task<JsonElement> GetStatusAsync(JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var targetUuid = RequireUuidArg(args, "uuid");
        var (kind, displayNameOverride) = await ResolveKindAsync(targetUuid, ct);
        if (kind == KindUnit)
        {
            await EnsureDirectoryReadAllowedAsync(context, targetUuid, ct);
        }

        // Resolve the display name through the same path BuildEntryAsync
        // uses so the slim projection's display_name field matches what a
        // sibling sv.directory.get_member call would have returned. The tenant
        // sentinel and human kinds skip the live-status probe entirely.
        string displayName;
        if (string.Equals(kind, KindTenant, StringComparison.Ordinal))
        {
            displayName = await ResolveTenantDisplayNameAsync(_tenantContext.CurrentTenantId, ct);
        }
        else if (!string.IsNullOrWhiteSpace(displayNameOverride))
        {
            displayName = displayNameOverride!;
        }
        else
        {
            var (_, dbDisplayName, _) = await ReadDefinitionAsync(targetUuid, kind, ct);
            displayName = !string.IsNullOrWhiteSpace(dbDisplayName)
                ? dbDisplayName!
                : await ResolveDisplayNameAsync(Address.For(kind, targetUuid), ct);
        }

        var liveStatus = await ResolveLiveStatusAsync(targetUuid, kind, ct);

        return SerializeStatusEntry(targetUuid, kind, displayName, liveStatus);
    }

    /// <summary>
    /// Resolves the unit whose members <c>sv.directory.list_members</c>
    /// lists. The uuid defaults to the caller only when the caller is a unit
    /// ("my members"); an agent has no members, so an agent omitting the uuid
    /// gets a retry-guiding error pointing at the right tool instead of a
    /// silently empty list (#3036).
    /// </summary>
    private static string ResolveListMembersUnitUuid(JsonElement args, ToolCallContext context)
    {
        var explicitUuid = TryReadUuidArg(args, "uuid");
        if (explicitUuid is not null)
        {
            return explicitUuid;
        }
        var (callerUuid, callerKind) = ParseCaller(context);
        if (!string.Equals(callerKind, KindUnit, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "sv.directory.list_members lists the members of a UNIT, but you are an agent and agents have no " +
                "members. Pass the unit's `uuid` — e.g. one of your parent_uuids from sv.directory.get_self — or " +
                "call sv.directory.list with scope='unit_members' to list your unit's members directly.");
        }
        return callerUuid;
    }

    private async Task<JsonElement> ListMembersAsync(JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var unitUuid = ResolveListMembersUnitUuid(args, context);
        var (limit, offset) = ParsePaging(args);
        await EnsureDirectoryReadAllowedAsync(context, unitUuid, ct);

        if (!GuidFormatter.TryParse(unitUuid, out var unitGuid))
        {
            throw new ArgumentException($"uuid '{unitUuid}' is not a parseable Guid.");
        }

        // Agent + sub-unit members from the existing member graph.
        var addresses = await _memberGraphStore.GetMembersAsync(unitGuid, ct);

        // ADR-0044 § 5: fold in package-declared human team members. One
        // entry per (unit, human) row per ADR-0046 §7 — a human filling
        // multiple team roles surfaces as a single entry with a
        // multi-valued roles array.
        var humanRows = await _humanMembershipStore.ListByUnitAsync(unitGuid, ct);

        // #3089 / ADR-0046 §8: agent entries surface their effective roles
        // — membership roles ∪ agent_definitions.role, deduped — resolved
        // once for the whole unit through the single member-role seam
        // instead of a per-entry definition read plus a parallel membership
        // pass. The seam's one join folds both role sources, so the
        // per-agent supplement below is a hash lookup. Sub-unit members
        // carry no member-level role metadata (the unit_subunit_memberships
        // table has no roles column), so sub-unit entries are unaffected.
        var effectiveRolesByAgentId = await _memberRoleDirectory.GetAgentMemberRolesAsync(unitGuid, ct);

        var totalCount = addresses.Count + humanRows.Count;

        // Paginate over the homogeneous concatenated list. Order is stable:
        // agents and sub-units first (their CreatedAt-ordered emit from
        // the member graph), then humans (CreatedAt-ordered from the
        // membership store). This keeps existing list-tool consumers
        // byte-for-byte stable when the unit has no human members and
        // surfaces the new entries deterministically when it does.
        var entries = new List<DirectoryEntry>();
        var skipped = 0;
        var taken = 0;

        foreach (var address in addresses)
        {
            if (taken >= limit) break;
            if (skipped < offset)
            {
                skipped++;
                continue;
            }
            var entry = await BuildEntryAsync(address.Path, address.Scheme, ct);

            // #3089: stamp the agent entry's effective roles from the
            // member-role seam. The seam already folds the agent's
            // definition-level role into the per-membership roles, so the
            // entry carries the final role set with no further fold needed
            // downstream. The agent's own seed-expertise list already
            // surfaces on entry.Expertise from BuildEntryAsync.
            if (string.Equals(address.Scheme, KindAgent, StringComparison.Ordinal)
                && GuidFormatter.TryParse(address.Path, out var agentGuid)
                && effectiveRolesByAgentId.TryGetValue(agentGuid, out var roles)
                && roles.Count > 0)
            {
                entry = entry with { Roles = roles };
            }

            entries.Add(entry);
            taken++;
        }

        foreach (var row in humanRows)
        {
            if (taken >= limit) break;
            if (skipped < offset)
            {
                skipped++;
                continue;
            }
            entries.Add(await BuildHumanEntryAsync(unitGuid, row, ct));
            taken++;
        }

        return SerializePagedList("members", entries, totalCount, limit, offset);
    }

    /// <summary>
    /// Renders one <c>unit_memberships_humans</c> row as a
    /// <see cref="DirectoryEntry"/> on the universal entry shape. The
    /// multi-valued <c>roles</c> list is carried on the entry's
    /// <see cref="DirectoryEntry.Roles"/> slot per ADR-0046 §9.
    /// </summary>
    private async Task<DirectoryEntry> BuildHumanEntryAsync(
        Guid unitGuid,
        UnitHumanMembership row,
        CancellationToken ct)
    {
        var humanUuid = GuidFormatter.Format(row.HumanId);
        var displayName = await ResolveHumanDisplayNameAsync(row.HumanId, ct);

        // Project the membership row's expertise tags onto the universal
        // ExpertiseDomain shape. Team-membership expertise has no level
        // attached — the description stays empty per ADR-0044 § 5.
        var expertise = row.Expertise
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => new ExpertiseDomain(e, Description: string.Empty, Level: null))
            .ToList();

        var roles = row.Roles
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToList();

        // ADR-0044 § 5 / #2491: humans have no runtime status — the
        // `live_status` field is omitted entirely from the wire shape
        // (null on the projection; the serializer skips writing the
        // property when LiveStatus is null).
        return new DirectoryEntry(
            Uuid: humanUuid,
            Kind: KindHuman,
            DisplayName: displayName,
            ParentUuids: new[] { GuidFormatter.Format(unitGuid) },
            Description: string.Empty,
            Expertise: expertise,
            MemberCount: null,
            LiveStatus: null,
            Roles: roles);
    }

    private async Task<string> ResolveHumanDisplayNameAsync(Guid humanId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var resolver = scope.ServiceProvider
            .GetRequiredService<Cvoya.Spring.Core.Security.IHumanIdentityResolver>();
        var display = await resolver.GetDisplayNameAsync(humanId, ct);
        return string.IsNullOrWhiteSpace(display) ? GuidFormatter.Format(humanId) : display!;
    }

    private async Task<JsonElement> GetSiblingsAsync(JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        // uuid defaults to the caller (#3036): "my siblings" is the obvious
        // intent of a no-arg call, and a caller — agent or unit — always has
        // a position in the unit graph to resolve siblings from.
        var targetUuid = TryReadUuidArg(args, "uuid") ?? ParseCaller(context).Uuid;
        var (limit, offset) = ParsePaging(args);
        var (targetKind, _) = await ResolveKindAsync(targetUuid, ct);

        // Resolve parents first so we can authz against each parent unit and
        // walk to its members.
        var parentUuids = await ResolveParentUuidsAsync(targetUuid, targetKind, ct);
        var siblingsByUuid = new Dictionary<string, DirectoryEntry>(StringComparer.Ordinal);

        foreach (var parentUuid in parentUuids)
        {
            // The tenant sentinel never carries a member list — it isn't a
            // unit and IUnitMemberGraphStore would return empty for the
            // tenant id. Skip authz + read entirely.
            if (string.Equals(parentUuid, GuidFormatter.Format(_tenantContext.CurrentTenantId), StringComparison.Ordinal))
            {
                continue;
            }

            if (!GuidFormatter.TryParse(parentUuid, out var parentGuid))
            {
                continue;
            }

            var allowed = await IsDirectoryReadAllowedAsync(context, parentGuid, ct);
            if (!allowed)
            {
                // Skip parents the caller can't read. Still emit the others.
                continue;
            }

            var siblings = await _memberGraphStore.GetMembersAsync(parentGuid, ct);
            foreach (var siblingAddress in siblings)
            {
                if (string.Equals(siblingAddress.Path, targetUuid, StringComparison.Ordinal))
                {
                    continue;
                }
                if (siblingsByUuid.ContainsKey(siblingAddress.Path))
                {
                    continue;
                }
                siblingsByUuid[siblingAddress.Path] = await BuildEntryAsync(
                    siblingAddress.Path, siblingAddress.Scheme, ct);
            }
        }

        var ordered = siblingsByUuid.Values.ToList();
        var page = ordered.Skip(offset).Take(limit).ToList();
        return SerializePagedList("siblings", page, ordered.Count, limit, offset);
    }

    private async Task<JsonElement> GetParentsAsync(JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var targetUuid = RequireUuidArg(args, "uuid");
        var (targetKind, _) = await ResolveKindAsync(targetUuid, ct);

        var parentUuids = await ResolveParentUuidsAsync(targetUuid, targetKind, ct);
        var entries = new List<DirectoryEntry>(parentUuids.Count);
        var tenantUuid = GuidFormatter.Format(_tenantContext.CurrentTenantId);

        foreach (var parentUuid in parentUuids)
        {
            if (string.Equals(parentUuid, tenantUuid, StringComparison.Ordinal))
            {
                entries.Add(await BuildTenantSentinelAsync(ct));
                continue;
            }

            // Parents of a unit are always units; parents of an agent in OSS
            // are always units too. Authz the read first, then build.
            if (GuidFormatter.TryParse(parentUuid, out var parentGuid))
            {
                var allowed = await IsDirectoryReadAllowedAsync(context, parentGuid, ct);
                if (!allowed)
                {
                    continue;
                }
                entries.Add(await BuildEntryAsync(parentUuid, KindUnit, ct));
            }
        }

        // Top-level entities (no parent edges in EF) still surface the
        // tenant sentinel so the contract is uniform — every reachable
        // entity has at least one parent.
        if (entries.Count == 0)
        {
            entries.Add(await BuildTenantSentinelAsync(ct));
        }

        return SerializeRawList(entries);
    }

    private async Task<DirectoryEntry> BuildEntryAsync(
        string uuid,
        string kind,
        CancellationToken ct,
        string? displayNameOverride = null)
    {
        if (string.Equals(kind, KindTenant, StringComparison.Ordinal))
        {
            return await BuildTenantSentinelAsync(ct);
        }

        var address = Address.For(kind, uuid);
        var (description, displayNameFromDb, agentRole) = await ReadDefinitionAsync(uuid, kind, ct);

        string displayName;
        if (!string.IsNullOrWhiteSpace(displayNameOverride))
        {
            displayName = displayNameOverride!;
        }
        else if (!string.IsNullOrWhiteSpace(displayNameFromDb))
        {
            displayName = displayNameFromDb!;
        }
        else
        {
            displayName = await ResolveDisplayNameAsync(address, ct);
        }

        var parentUuids = await ResolveParentUuidsAsync(uuid, kind, ct);

        var expertise = string.Equals(kind, KindUnit, StringComparison.Ordinal)
            || string.Equals(kind, KindAgent, StringComparison.Ordinal)
            ? await _expertiseStore.GetDomainsAsync(address, ct)
            : Array.Empty<ExpertiseDomain>();

        int? memberCount = null;
        if (string.Equals(kind, KindUnit, StringComparison.Ordinal)
            && GuidFormatter.TryParse(uuid, out var unitGuid))
        {
            // GetMembersAsync is the v0.1-pragmatic count source — folds the
            // agent + sub-unit edge tables in one read. A dedicated COUNT
            // method on IUnitMemberGraphStore is the natural follow-up if
            // unit sizes grow.
            var members = await _memberGraphStore.GetMembersAsync(unitGuid, ct);
            memberCount = members.Count;
        }

        // #2491: live_status carries the actor's per-thread channel
        // snapshot for agent / unit kinds. Returns null on any failure
        // (unreachable actor, unsupported kind) so the serializer omits
        // the property — the wire shape distinguishes "no runtime"
        // from "runtime reported zero".
        var liveStatus = await ResolveLiveStatusAsync(uuid, kind, ct);

        // #3089 / #3086: the entry's effective roles. For the single-entry
        // build there is no containing-unit context, so the effective set
        // is just the agent's own definition-level role
        // (agent_definitions.role) run through the shared
        // EffectiveRolePolicy rule. The per-unit list surfaces
        // (list_members / list) overwrite Roles afterward with the unit's
        // membership-roles ∪ definition-role union from the member-role
        // seam. Unit / human members carry no agent-definition role, so
        // their Roles stay null here (humans set theirs in BuildHumanEntryAsync).
        var roles = string.Equals(kind, KindAgent, StringComparison.Ordinal)
            ? EffectiveRolePolicy.Combine(null, agentRole)
            : null;

        return new DirectoryEntry(
            Uuid: uuid,
            Kind: kind,
            DisplayName: displayName,
            ParentUuids: parentUuids,
            Description: description,
            Expertise: expertise,
            MemberCount: memberCount,
            LiveStatus: liveStatus,
            Roles: roles);
    }

    private async Task<DirectoryEntry> BuildTenantSentinelAsync(CancellationToken ct)
    {
        var tenantGuid = _tenantContext.CurrentTenantId;
        var tenantUuid = GuidFormatter.Format(tenantGuid);
        var tenantDisplayName = await ResolveTenantDisplayNameAsync(tenantGuid, ct);

        // The tenant sentinel is non-addressable and has no runtime —
        // `live_status` is omitted from the wire shape (null on the
        // projection; the serializer skips writing the property).
        return new DirectoryEntry(
            Uuid: tenantUuid,
            Kind: KindTenant,
            DisplayName: tenantDisplayName,
            ParentUuids: Array.Empty<string>(),
            Description: "Top-level tenant marker. Non-addressable — present only so callers can distinguish 'has a unit parent' from 'has a unit parent AND is also at the top level'.",
            Expertise: Array.Empty<ExpertiseDomain>(),
            MemberCount: null,
            LiveStatus: null);
    }

    private async Task<(string Description, string? DisplayName, string? AgentRole)> ReadDefinitionAsync(string uuid, string kind, CancellationToken ct)
    {
        if (!GuidFormatter.TryParse(uuid, out var guid))
        {
            return (string.Empty, null, null);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        if (string.Equals(kind, KindAgent, StringComparison.Ordinal))
        {
            // #3086: read the agent's definition-level role alongside its
            // description / display name in the same round-trip so the
            // effective-roles projection can fold it into the entry's roles
            // list. The display_name already comes from this lookup, so the
            // role read is free.
            var row = await db.AgentDefinitions
                .Where(a => a.Id == guid)
                .Select(a => new { a.Description, a.DisplayName, a.Role })
                .FirstOrDefaultAsync(ct);
            return (row?.Description ?? string.Empty, row?.DisplayName, row?.Role);
        }

        if (string.Equals(kind, KindUnit, StringComparison.Ordinal))
        {
            var row = await db.UnitDefinitions
                .Where(u => u.Id == guid)
                .Select(u => new { u.Description, u.DisplayName })
                .FirstOrDefaultAsync(ct);
            return (row?.Description ?? string.Empty, row?.DisplayName, null);
        }

        return (string.Empty, null, null);
    }

    private async Task<string> ResolveDisplayNameAsync(Address address, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<Cvoya.Spring.Core.Security.IParticipantDisplayNameResolver>();
        return await resolver.ResolveAsync(address.ToString(), ct);
    }

    private async Task<string> ResolveTenantDisplayNameAsync(Guid tenantId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var name = await db.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.DisplayName)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(name) ? "(tenant root)" : name!;
    }

    private async Task<IReadOnlyList<string>> ResolveParentUuidsAsync(string uuid, string kind, CancellationToken ct)
    {
        if (!GuidFormatter.TryParse(uuid, out var guid))
        {
            return Array.Empty<string>();
        }

        using var scope = _scopeFactory.CreateScope();

        if (string.Equals(kind, KindAgent, StringComparison.Ordinal))
        {
            var memberships = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
            var rows = await memberships.ListByAgentAsync(guid, ct);
            return rows.Select(r => GuidFormatter.Format(r.UnitId))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        if (string.Equals(kind, KindUnit, StringComparison.Ordinal))
        {
            var subunits = scope.ServiceProvider.GetRequiredService<IUnitSubunitMembershipRepository>();
            var rows = await subunits.ListByChildAsync(guid, ct);
            return rows.Select(r => GuidFormatter.Format(r.ParentId))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        return Array.Empty<string>();
    }

    private async Task<(string Kind, string? DisplayName)> ResolveKindAsync(string uuid, CancellationToken ct)
    {
        if (!GuidFormatter.TryParse(uuid, out var guid))
        {
            throw new ArgumentException($"Invalid uuid '{uuid}'.");
        }

        var tenantUuid = _tenantContext.CurrentTenantId;
        if (guid == tenantUuid)
        {
            return (KindTenant, null);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var agent = await db.AgentDefinitions
            .Where(a => a.Id == guid)
            .Select(a => new { a.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (agent is not null)
        {
            return (KindAgent, agent.DisplayName);
        }

        var unit = await db.UnitDefinitions
            .Where(u => u.Id == guid)
            .Select(u => new { u.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (unit is not null)
        {
            return (KindUnit, unit.DisplayName);
        }

        throw new ArgumentException(
            $"No agent, unit, or tenant in the current scope matches uuid '{uuid}'.");
    }

    private async Task EnsureDirectoryReadAllowedAsync(ToolCallContext context, string targetUnitUuid, CancellationToken ct)
    {
        if (!await IsDirectoryReadAllowedAsync(context, targetUnitUuid, ct))
        {
            throw new SpringException(
                $"Caller '{context.CallerId}' is not authorised to read the directory of unit '{targetUnitUuid}'.");
        }
    }

    private Task<bool> IsDirectoryReadAllowedAsync(ToolCallContext context, string targetUnitUuid, CancellationToken ct)
    {
        if (!GuidFormatter.TryParse(targetUnitUuid, out var unitGuid))
        {
            return Task.FromResult(false);
        }
        return IsDirectoryReadAllowedAsync(context, unitGuid, ct);
    }

    private async Task<bool> IsDirectoryReadAllowedAsync(ToolCallContext context, Guid targetUnitGuid, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var enforcer = scope.ServiceProvider.GetRequiredService<IUnitPolicyEnforcer>();
        var verdict = await enforcer.EvaluateUnitDirectoryReadAsync(context.CallerId, targetUnitGuid, ct);
        if (!verdict.IsAllowed)
        {
            _logger.LogInformation(
                "Directory read denied for caller {CallerId} on unit {UnitId}: {Reason}",
                context.CallerId, targetUnitGuid, verdict.Reason);
        }
        return verdict.IsAllowed;
    }

    /// <summary>
    /// Resolves the advisory runtime-status snapshot for the supplied
    /// subject (#2491). For agents and units this round-trips through the
    /// actor proxy to <c>GetRuntimeStatusAsync</c>; for any other kind
    /// (human, tenant, unknown) returns <c>null</c> so the caller omits
    /// the field from the wire shape entirely.
    /// </summary>
    /// <remarks>
    /// Per-subject failures are tolerated: a Dapr remoting hiccup, an
    /// actor that hasn't been activated, or any other transient error
    /// surfaces as <c>null</c> rather than propagating. Status is
    /// advisory — a stale or missing snapshot is correct by design;
    /// the actor mailbox is the ordering authority.
    /// </remarks>
    private async Task<AgentRuntimeStatusReport?> ResolveLiveStatusAsync(
        string uuid, string kind, CancellationToken ct)
    {
        if (!GuidFormatter.TryParse(uuid, out var guid))
        {
            return null;
        }

        try
        {
            var actorId = new ActorId(GuidFormatter.Format(guid));
            if (string.Equals(kind, KindAgent, StringComparison.Ordinal))
            {
                var proxy = _actorProxyFactory.CreateActorProxy<IAgentActor>(
                    actorId, nameof(AgentActor));
                return await proxy.GetRuntimeStatusAsync(ct);
            }

            if (string.Equals(kind, KindUnit, StringComparison.Ordinal))
            {
                var proxy = _actorProxyFactory.CreateActorProxy<IUnitActor>(
                    actorId, nameof(UnitActor));
                return await proxy.GetRuntimeStatusAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort indicator — per-subject failures must not break
            // the surrounding list / single-entry response. Log at
            // Information so operators can see runtime-status read
            // failures without crowding the default Warning channel.
            _logger.LogInformation(ex,
                "live_status read failed for {Kind} uuid {Uuid}; omitting from response.",
                kind, uuid);
        }

        return null;
    }

    private static (string Uuid, string Kind) ParseCaller(ToolCallContext context)
    {
        if (string.IsNullOrWhiteSpace(context.CallerId))
        {
            throw new SpringException("Tool call context is missing the caller id.");
        }
        if (!GuidFormatter.TryParse(context.CallerId, out var guid))
        {
            throw new SpringException($"Caller id '{context.CallerId}' is not a parseable Guid.");
        }
        var kind = string.IsNullOrWhiteSpace(context.CallerKind) ? KindAgent : context.CallerKind;
        return (GuidFormatter.Format(guid), kind);
    }

    private static string RequireUuidArg(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var prop) ||
            prop.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException(
                $"sv.directory.* requires a '{name}': the no-dash 32-char hex (or dashed) UUID of the agent or " +
                "unit to inspect. Get one from sv.directory.get_self, sv.directory.list, sv.directory.get_parents, " +
                "or the sender address on an inbound message.");
        }
        var raw = prop.GetString();
        if (string.IsNullOrWhiteSpace(raw) || !GuidFormatter.TryParse(raw, out var guid))
        {
            throw new ArgumentException(
                $"Argument '{name}' = '{raw}' is not a valid UUID. Pass the no-dash 32-char hex (or dashed) UUID " +
                "of an agent or unit (e.g. a uuid from sv.directory.get_self or sv.directory.list).");
        }
        return GuidFormatter.Format(guid);
    }

    /// <summary>
    /// Reads an optional <c>uuid</c> argument for the list_members /
    /// get_siblings default-to-caller path (#3036). Returns <c>null</c> when
    /// the argument is absent (the caller substitutes a default), but a
    /// present-but-malformed value still throws a retry-guiding error rather
    /// than silently falling back to the caller — a botched id is a mistake
    /// to surface, not to paper over.
    /// </summary>
    private static string? TryReadUuidArg(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var prop))
        {
            return null;
        }
        var raw = prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
        if (string.IsNullOrWhiteSpace(raw) || !GuidFormatter.TryParse(raw, out var guid))
        {
            throw new ArgumentException(
                $"Argument '{name}' = '{raw}' is not a valid UUID. Pass the no-dash 32-char hex (or dashed) UUID " +
                "of an agent or unit, or omit it to default to yourself.");
        }
        return GuidFormatter.Format(guid);
    }

    private static (int Limit, int Offset) ParsePaging(JsonElement args)
    {
        var limit = DefaultLimit;
        var offset = 0;
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var lv))
            {
                limit = lv;
            }
            if (args.TryGetProperty("offset", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out var ov))
            {
                offset = ov;
            }
        }
        if (limit < 1) limit = 1;
        if (limit > MaxLimit) limit = MaxLimit;
        if (offset < 0) offset = 0;
        return (limit, offset);
    }

    private static JsonElement ParseSchema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Serializes the slim <c>sv.directory.get_status</c> projection (#2491):
    /// <c>{ uuid, kind, display_name, live_status? }</c>. Mirrors the
    /// omit-on-null contract of the directory entry's <c>live_status</c>
    /// field — humans and unreachable actors produce a response without
    /// the property.
    /// </summary>
    private static JsonElement SerializeStatusEntry(
        string uuid, string kind, string displayName, AgentRuntimeStatusReport? liveStatus)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("uuid", uuid);
            writer.WriteString("kind", kind);
            writer.WriteString("display_name", displayName);
            if (liveStatus is { } report)
            {
                writer.WritePropertyName("live_status");
                WriteLiveStatus(writer, report);
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement SerializeEntry(DirectoryEntry entry)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteEntry(writer, entry);
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement SerializeRawList(IReadOnlyList<DirectoryEntry> entries)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var entry in entries)
            {
                WriteEntry(writer, entry);
            }
            writer.WriteEndArray();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement SerializePagedList(string listProperty, IReadOnlyList<DirectoryEntry> entries, int totalCount, int limit, int offset)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName(listProperty);
            writer.WriteStartArray();
            foreach (var entry in entries)
            {
                WriteEntry(writer, entry);
            }
            writer.WriteEndArray();
            writer.WriteNumber("total_count", totalCount);
            writer.WriteNumber("limit", limit);
            writer.WriteNumber("offset", offset);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static void WriteEntry(Utf8JsonWriter writer, DirectoryEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("uuid", entry.Uuid);
        writer.WriteString("kind", entry.Kind);
        writer.WriteString("display_name", entry.DisplayName);
        writer.WritePropertyName("parent_uuids");
        writer.WriteStartArray();
        foreach (var parent in entry.ParentUuids)
        {
            writer.WriteStringValue(parent);
        }
        writer.WriteEndArray();
        writer.WriteString("description", entry.Description);
        writer.WritePropertyName("expertise");
        writer.WriteStartArray();
        foreach (var domain in entry.Expertise)
        {
            writer.WriteStartObject();
            writer.WriteString("name", domain.Name);
            writer.WriteString("description", domain.Description ?? string.Empty);
            if (domain.Level is { } level)
            {
                writer.WriteString("level", level.ToString().ToLowerInvariant());
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (entry.MemberCount is { } count)
        {
            writer.WriteNumber("member_count", count);
        }
        else
        {
            writer.WriteNull("member_count");
        }
        // #2491: omit `live_status` entirely when the projection carries
        // no report (humans, tenant sentinel, and any agent / unit whose
        // actor proxy call failed). The field's absence is the contract
        // — callers MUST treat missing == "no runtime here" rather than
        // assume zero.
        if (entry.LiveStatus is { } report)
        {
            writer.WritePropertyName("live_status");
            WriteLiveStatus(writer, report);
        }

        // ADR-0046 §9: emit `roles: string[]` uniformly on every entry
        // kind (agent / unit / human), serialising as `[]` when the entry
        // has no roles. Replaces the ADR-0044 `team_role: string` field on
        // human entries; additive on agent / unit entries (the field was
        // absent before). Uniform shape lets clients treat `roles` as a
        // stable `string[]` without distinguishing missing from empty.
        // #3089 / #3086: the entry's Roles is already the effective set
        // (membership roles ∪ the agent's definition-level role), resolved
        // once by the member-role seam, so role-based delegation resolves
        // even when the membership row carries no roles.
        writer.WritePropertyName("roles");
        writer.WriteStartArray();
        foreach (var role in EntryRoles(entry))
        {
            writer.WriteStringValue(role);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes one <see cref="AgentRuntimeStatusReport"/> as the
    /// <c>live_status</c> object (#2491). Field names mirror what the
    /// <c>sv.directory.get_status</c> tool description advertises: <c>in_flight</c>,
    /// <c>queued</c>, <c>channels</c>, <c>observed_at</c>.
    /// </summary>
    private static void WriteLiveStatus(Utf8JsonWriter writer, AgentRuntimeStatusReport report)
    {
        writer.WriteStartObject();
        writer.WriteNumber("in_flight", report.InFlightThreadCount);
        writer.WriteNumber("queued", report.QueuedMessageCount);
        writer.WriteNumber("channels", report.ChannelCount);
        writer.WriteString("observed_at", report.ObservedAt.ToString("O"));
        writer.WriteEndObject();
    }

    private sealed record DirectoryEntry(
        string Uuid,
        string Kind,
        string DisplayName,
        IReadOnlyList<string> ParentUuids,
        string Description,
        IReadOnlyList<ExpertiseDomain> Expertise,
        int? MemberCount,
        AgentRuntimeStatusReport? LiveStatus,
        IReadOnlyList<string>? Roles = null);

    /// <summary>
    /// The effective roles surfaced on an entry's wire <c>roles</c> array.
    /// #3089 made the entry's <see cref="DirectoryEntry.Roles"/> the
    /// already-resolved effective set — list surfaces stamp the per-unit
    /// union from <see cref="IUnitMemberRoleDirectory"/>, single-entry
    /// surfaces stamp the agent's definition-level role via
    /// <see cref="EffectiveRolePolicy.Combine"/> in
    /// <see cref="BuildEntryAsync"/>, and humans set theirs in
    /// <see cref="BuildHumanEntryAsync"/> — so this is a straight read
    /// with a null-coalesce, no fold.
    /// </summary>
    private static IReadOnlyList<string> EntryRoles(DirectoryEntry entry) =>
        entry.Roles ?? Array.Empty<string>();
}
