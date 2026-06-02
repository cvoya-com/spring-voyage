// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Memory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Mvc;

using CoreMemoryEntry = Cvoya.Spring.Core.Memory.MemoryEntry;
using WireMemoryEntry = Cvoya.Spring.Host.Api.Models.MemoryEntry;

/// <summary>
/// Maps the memory inspector read API (#2342). Three routes per
/// addressable kind (unit / agent):
/// <c>GET /api/v1/tenant/{units|agents}/{id}/memories</c> lists or
/// searches entries (FTS via the optional <c>?query=</c> param);
/// <c>GET /api/v1/tenant/{units|agents}/{id}/memories/{memoryId}</c>
/// reads a single entry. All routes resolve through
/// <see cref="IDirectoryService"/> first; an unknown agent / unit
/// returns 404, as does a memory id that is not owned by the
/// addressable. Write paths live on the <c>sv.memory_*</c>
/// agent-runtime tools, not on the HTTP surface (operator writes are
/// tracked under v0.2 #2357).
/// </summary>
public static class MemoriesEndpoints
{
    /// <summary>Default page size for the list endpoint.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Maximum allowed page size.</summary>
    public const int MaxLimit = 500;

    private const string ScopeAgent = "agent";
    private const string ScopeThread = "thread";

    /// <summary>
    /// Registers the memory endpoints. Call from <c>Program.cs</c>
    /// after <c>MapTenantTreeEndpoints</c>. Returns a single
    /// <see cref="RouteGroupBuilder"/> so callers can apply
    /// <c>RequireAuthorization()</c> uniformly.
    /// </summary>
    public static RouteGroupBuilder MapMemoriesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(string.Empty);

        group.MapGet("/api/v1/tenant/units/{id}/memories", GetUnitMemoriesAsync)
            .WithTags("Units")
            .WithName("GetUnitMemories")
            .WithSummary("Read the unit's agent- and thread-scoped memory entries (#2342)")
            .Produces<MemoriesResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/api/v1/tenant/agents/{id}/memories", GetAgentMemoriesAsync)
            .WithTags("Agents")
            .WithName("GetAgentMemories")
            .WithSummary("Read the agent's agent- and thread-scoped memory entries (#2342)")
            .Produces<MemoriesResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/api/v1/tenant/units/{id}/memories/{memoryId}", GetUnitMemoryAsync)
            .WithTags("Units")
            .WithName("GetUnitMemory")
            .WithSummary("Read a single memory entry by id, scoped to the unit (#2342)")
            .Produces<WireMemoryEntry>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/api/v1/tenant/agents/{id}/memories/{memoryId}", GetAgentMemoryAsync)
            .WithTags("Agents")
            .WithName("GetAgentMemory")
            .WithSummary("Read a single memory entry by id, scoped to the agent (#2342)")
            .Produces<WireMemoryEntry>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static Task<IResult> GetUnitMemoriesAsync(
        string id,
        [FromQuery] string? scope,
        [FromQuery] string? query,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IMemoryStore memoryStore,
        CancellationToken cancellationToken)
        => GetMemoriesAsync(Address.UnitScheme, id, scope, query, limit, offset,
            directoryService, memoryStore, cancellationToken);

    private static Task<IResult> GetAgentMemoriesAsync(
        string id,
        [FromQuery] string? scope,
        [FromQuery] string? query,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IMemoryStore memoryStore,
        CancellationToken cancellationToken)
        => GetMemoriesAsync(Address.AgentScheme, id, scope, query, limit, offset,
            directoryService, memoryStore, cancellationToken);

    private static Task<IResult> GetUnitMemoryAsync(
        string id,
        Guid memoryId,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IMemoryStore memoryStore,
        CancellationToken cancellationToken)
        => GetSingleMemoryAsync(Address.UnitScheme, id, memoryId,
            directoryService, memoryStore, cancellationToken);

    private static Task<IResult> GetAgentMemoryAsync(
        string id,
        Guid memoryId,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IMemoryStore memoryStore,
        CancellationToken cancellationToken)
        => GetSingleMemoryAsync(Address.AgentScheme, id, memoryId,
            directoryService, memoryStore, cancellationToken);

    private static async Task<IResult> GetMemoriesAsync(
        string scheme,
        string id,
        string? scopeArg,
        string? queryArg,
        int? limitArg,
        int? offsetArg,
        IDirectoryService directoryService,
        IMemoryStore memoryStore,
        CancellationToken cancellationToken)
    {
        var address = Address.For(scheme, id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return NotFoundFor(scheme, id);
        }

        var scopeFilter = ParseScopeArg(scopeArg);
        if (scopeArg is not null && scopeFilter is null)
        {
            return Results.Problem(
                detail: $"Query 'scope' must be '{ScopeAgent}' or '{ScopeThread}'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var (limit, offset) = NormalisePaging(limitArg, offsetArg);

        // When `query` is supplied, route through the FTS path. The
        // store ignores `offset` on search (relevance ordering makes
        // paging meaningless without a stable cursor); we surface
        // `limit` only. The list path keeps offset-based paging. The
        // operator inspector sees every thread's entries, so the
        // per-thread recall filter is disabled (recallThreadId: null).
        IReadOnlyList<CoreMemoryEntry> entries;
        if (!string.IsNullOrWhiteSpace(queryArg))
        {
            entries = await memoryStore.SearchAsync(
                address, queryArg, scopeFilter, recallThreadId: null, limit, cancellationToken);
        }
        else
        {
            entries = await memoryStore.ListAsync(
                address, scopeFilter, recallThreadId: null, limit, offset, cancellationToken);
        }

        // Partition into agent/thread arrays in-memory. The list already
        // filtered down to the requested scope if one was supplied, so
        // the other side surfaces as empty — the wire shape stays stable.
        var agent = new List<WireMemoryEntry>();
        var thread = new List<WireMemoryEntry>();
        foreach (var row in entries)
        {
            var wire = ToWire(row);
            if (row.Scope == MemoryScope.Thread)
            {
                thread.Add(wire);
            }
            else
            {
                agent.Add(wire);
            }
        }

        return Results.Ok(new MemoriesResponse(agent, thread));
    }

    private static async Task<IResult> GetSingleMemoryAsync(
        string scheme,
        string id,
        Guid memoryId,
        IDirectoryService directoryService,
        IMemoryStore memoryStore,
        CancellationToken cancellationToken)
    {
        var address = Address.For(scheme, id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return NotFoundFor(scheme, id);
        }

        var row = await memoryStore.GetAsync(address, memoryId, cancellationToken);
        if (row is null)
        {
            return Results.Problem(
                detail: $"Memory '{memoryId:N}' not found.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(ToWire(row));
    }

    private static IResult NotFoundFor(string scheme, string id) =>
        Results.Problem(
            detail: $"{char.ToUpperInvariant(scheme[0])}{scheme[1..]} '{id}' not found",
            statusCode: StatusCodes.Status404NotFound);

    private static MemoryScope? ParseScopeArg(string? scopeArg)
    {
        if (string.IsNullOrEmpty(scopeArg))
        {
            return null;
        }
        if (string.Equals(scopeArg, ScopeAgent, StringComparison.Ordinal))
        {
            return MemoryScope.Agent;
        }
        if (string.Equals(scopeArg, ScopeThread, StringComparison.Ordinal))
        {
            return MemoryScope.Thread;
        }
        return null;
    }

    private static (int Limit, int Offset) NormalisePaging(int? limit, int? offset)
    {
        var l = limit ?? DefaultLimit;
        if (l < 1) l = 1;
        if (l > MaxLimit) l = MaxLimit;
        var o = offset ?? 0;
        if (o < 0) o = 0;
        return (l, o);
    }

    private static WireMemoryEntry ToWire(CoreMemoryEntry entry) =>
        new(
            Id: GuidFormatter.Format(entry.Id),
            Content: entry.Content,
            CreatedAt: entry.CreatedAt,
            Source: entry.Source,
            Scope: entry.Scope == MemoryScope.Thread ? ScopeThread : ScopeAgent,
            UpdatedAt: entry.UpdatedAt,
            ThreadId: entry.ThreadId is { } tid ? GuidFormatter.Format(tid) : null);
}
