// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Execution;

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
/// member_count, live_status }</c>. <c>kind</c> is one of <c>"agent"</c>,
/// <c>"unit"</c>, or <c>"tenant"</c>. <c>parent_uuids</c> is always a list
/// (possibly empty for the tenant sentinel). <c>member_count</c> is
/// populated only for unit-kind entries; <c>expertise</c> is empty for the
/// tenant sentinel.
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
/// can also read it via <c>sv.get_self()</c>.
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
    /// <summary>Tool name for <c>sv.get_self</c>.</summary>
    public const string GetSelfTool = "sv.get_self";

    /// <summary>Tool name for <c>sv.get_member</c>.</summary>
    public const string GetMemberTool = "sv.get_member";

    /// <summary>Tool name for <c>sv.list_members</c>.</summary>
    public const string ListMembersTool = "sv.list_members";

    /// <summary>Tool name for <c>sv.get_siblings</c>.</summary>
    public const string GetSiblingsTool = "sv.get_siblings";

    /// <summary>Tool name for <c>sv.get_parents</c>.</summary>
    public const string GetParentsTool = "sv.get_parents";

    /// <summary>Default page size for list tools, mirroring DirectorySearchResponse.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Maximum allowed page size, mirroring DirectorySearchResponse.</summary>
    public const int MaxLimit = 200;

    private const string KindAgent = Address.AgentScheme;
    private const string KindUnit = Address.UnitScheme;
    private const string KindTenant = "tenant";

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
          "required": ["uuid"],
          "properties": {
            "uuid": { "type": "string", "description": "Stable Guid identifier." },
            "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 50 },
            "offset": { "type": "integer", "minimum": 0, "default": 0 }
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
    private readonly IExpertiseStore _expertiseStore;
    private readonly PersistentAgentRegistry _agentRegistry;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger _logger;

    private readonly IReadOnlyList<ToolDefinition> _tools;

    /// <summary>Builds the registry with its singleton dependencies.</summary>
    public SvDirectorySkillRegistry(
        IServiceScopeFactory scopeFactory,
        IUnitMemberGraphStore memberGraphStore,
        IExpertiseStore expertiseStore,
        PersistentAgentRegistry agentRegistry,
        ITenantContext tenantContext,
        ILoggerFactory loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _memberGraphStore = memberGraphStore;
        _expertiseStore = expertiseStore;
        _agentRegistry = agentRegistry;
        _tenantContext = tenantContext;
        _logger = loggerFactory.CreateLogger<SvDirectorySkillRegistry>();

        _tools = new[]
        {
            new ToolDefinition(
                GetSelfTool,
                "Returns metadata for the calling agent or unit (the entity whose runtime is " +
                "executing this tool call). Output is a single entry: " +
                "{ uuid, kind, display_name, parent_uuids, description, expertise, member_count, live_status }. " +
                "Use this as the bootstrap when navigating the unit hierarchy — the entry's " +
                "parent_uuids are the starting point for sv.get_parents / sv.list_members.",
                EmptyObjectSchema),
            new ToolDefinition(
                GetMemberTool,
                "Returns metadata for a single agent or unit identified by uuid. Output shape " +
                "matches sv.get_self. " + TenantSentinelWarning,
                UuidArgSchema),
            new ToolDefinition(
                ListMembersTool,
                "Returns the direct members of the unit identified by uuid: a flat list mixing " +
                "agent-kind and unit-kind entries (filter by entry.kind on the client side if " +
                "you only want one). Sub-unit members are NOT recursively expanded — call " +
                "sv.list_members again on a sub-unit's uuid to walk further. Pagination via " +
                "limit (default 50, max 200) and offset; total_count carries the unfiltered total.",
                UuidPagedArgSchema),
            new ToolDefinition(
                GetSiblingsTool,
                "Returns entities that share at least one parent with the entity identified by " +
                "uuid. Self is excluded. Each returned entry carries its own parent_uuids so " +
                "the caller can filter by overlap with the target's parents to scope to a " +
                "specific context (e.g. 'siblings under the unit that just messaged me'). " +
                "Pagination via limit (default 50, max 200) and offset.",
                UuidPagedArgSchema),
            new ToolDefinition(
                GetParentsTool,
                "Returns the parents of the entity identified by uuid. For top-level units the " +
                "list contains the tenant sentinel entry (kind='tenant'). " + TenantSentinelWarning,
                UuidArgSchema),
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
            default:
                throw new SkillNotFoundException(toolName);
        }
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

    private async Task<JsonElement> ListMembersAsync(JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var unitUuid = RequireUuidArg(args, "uuid");
        var (limit, offset) = ParsePaging(args);
        await EnsureDirectoryReadAllowedAsync(context, unitUuid, ct);

        if (!GuidFormatter.TryParse(unitUuid, out var unitGuid))
        {
            throw new ArgumentException($"uuid '{unitUuid}' is not a parseable Guid.");
        }
        var addresses = await _memberGraphStore.GetMembersAsync(unitGuid, ct);
        var page = addresses.Skip(offset).Take(limit).ToList();
        var entries = new List<DirectoryEntry>(page.Count);
        foreach (var address in page)
        {
            entries.Add(await BuildEntryAsync(address.Path, address.Scheme, ct));
        }

        return SerializePagedList("members", entries, addresses.Count, limit, offset);
    }

    private async Task<JsonElement> GetSiblingsAsync(JsonElement args, ToolCallContext context, CancellationToken ct)
    {
        var targetUuid = RequireUuidArg(args, "uuid");
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
        var (description, displayNameFromDb) = await ReadDefinitionAsync(uuid, kind, ct);

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

        var liveStatus = ResolveLiveStatus(uuid);

        return new DirectoryEntry(
            Uuid: uuid,
            Kind: kind,
            DisplayName: displayName,
            ParentUuids: parentUuids,
            Description: description,
            Expertise: expertise,
            MemberCount: memberCount,
            LiveStatus: liveStatus);
    }

    private async Task<DirectoryEntry> BuildTenantSentinelAsync(CancellationToken ct)
    {
        var tenantGuid = _tenantContext.CurrentTenantId;
        var tenantUuid = GuidFormatter.Format(tenantGuid);
        var tenantDisplayName = await ResolveTenantDisplayNameAsync(tenantGuid, ct);

        return new DirectoryEntry(
            Uuid: tenantUuid,
            Kind: KindTenant,
            DisplayName: tenantDisplayName,
            ParentUuids: Array.Empty<string>(),
            Description: "Top-level tenant marker. Non-addressable — present only so callers can distinguish 'has a unit parent' from 'has a unit parent AND is also at the top level'.",
            Expertise: Array.Empty<ExpertiseDomain>(),
            MemberCount: null,
            LiveStatus: "n/a");
    }

    private async Task<(string Description, string? DisplayName)> ReadDefinitionAsync(string uuid, string kind, CancellationToken ct)
    {
        if (!GuidFormatter.TryParse(uuid, out var guid))
        {
            return (string.Empty, null);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        if (string.Equals(kind, KindAgent, StringComparison.Ordinal))
        {
            var row = await db.AgentDefinitions
                .Where(a => a.Id == guid)
                .Select(a => new { a.Description, a.DisplayName })
                .FirstOrDefaultAsync(ct);
            return (row?.Description ?? string.Empty, row?.DisplayName);
        }

        if (string.Equals(kind, KindUnit, StringComparison.Ordinal))
        {
            var row = await db.UnitDefinitions
                .Where(u => u.Id == guid)
                .Select(u => new { u.Description, u.DisplayName })
                .FirstOrDefaultAsync(ct);
            return (row?.Description ?? string.Empty, row?.DisplayName);
        }

        return (string.Empty, null);
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

    private string ResolveLiveStatus(string uuid)
    {
        if (!_agentRegistry.TryGet(uuid, out var entry) || entry is null)
        {
            return "unknown";
        }

        return entry.HealthStatus switch
        {
            AgentHealthStatus.Healthy => "online",
            AgentHealthStatus.Unhealthy => "unhealthy",
            _ => "unknown",
        };
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
            throw new ArgumentException($"Missing required argument '{name}'.");
        }
        var raw = prop.GetString();
        if (string.IsNullOrWhiteSpace(raw) || !GuidFormatter.TryParse(raw, out var guid))
        {
            throw new ArgumentException($"Argument '{name}' must be a Guid.");
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
        writer.WriteString("live_status", entry.LiveStatus);
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
        string LiveStatus);
}
