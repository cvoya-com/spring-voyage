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
using CoreMemoryTopic = Cvoya.Spring.Core.Memory.MemoryTopic;
using WireMemoryEntry = Cvoya.Spring.Host.Api.Models.MemoryEntry;
using WireMemoryTopic = Cvoya.Spring.Host.Api.Models.MemoryTopic;

/// <summary>
/// Maps the memory inspector read API (#2342). Two endpoint pairs mirror
/// the Explorer's Memory and Topics tabs:
/// <c>GET /api/v1/tenant/{units|agents}/{id}/memories</c> returns the
/// short-term and long-term entries; <c>/topics</c> returns the topic
/// inventory. Both routes route through <see cref="IDirectoryService"/>
/// to translate the addressed id into a real entry; an unknown agent /
/// unit returns 404. Write paths live on the <c>sv.memory_*</c> /
/// <c>sv.topic_*</c> agent-runtime tools, not on the HTTP surface.
/// </summary>
public static class MemoriesEndpoints
{
    /// <summary>Default page size for both endpoints.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Maximum allowed page size.</summary>
    public const int MaxLimit = 500;

    private const string KindLongTerm = "long_term";
    private const string KindShortTerm = "short_term";

    /// <summary>
    /// Registers the memory + topic endpoints. Call from
    /// <c>Program.cs</c> after <c>MapTenantTreeEndpoints</c>. Returns a
    /// single <see cref="RouteGroupBuilder"/> so callers can apply
    /// <c>RequireAuthorization()</c> uniformly.
    /// </summary>
    public static RouteGroupBuilder MapMemoriesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(string.Empty);

        group.MapGet("/api/v1/tenant/units/{id}/memories", GetUnitMemoriesAsync)
            .WithTags("Units")
            .WithName("GetUnitMemories")
            .WithSummary("Read the unit's short-term and long-term memory entries (#2342)")
            .Produces<MemoriesResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/api/v1/tenant/agents/{id}/memories", GetAgentMemoriesAsync)
            .WithTags("Agents")
            .WithName("GetAgentMemories")
            .WithSummary("Read the agent's short-term and long-term memory entries (#2342)")
            .Produces<MemoriesResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/api/v1/tenant/units/{id}/topics", GetUnitTopicsAsync)
            .WithTags("Units")
            .WithName("GetUnitTopics")
            .WithSummary("Read the unit's memory topics (#2342)")
            .Produces<TopicsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/api/v1/tenant/agents/{id}/topics", GetAgentTopicsAsync)
            .WithTags("Agents")
            .WithName("GetAgentTopics")
            .WithSummary("Read the agent's memory topics (#2342)")
            .Produces<TopicsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static Task<IResult> GetUnitMemoriesAsync(
        string id,
        [FromQuery] string? kind,
        [FromQuery] Guid? topicId,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IMemoryStore memoryStore,
        CancellationToken cancellationToken)
        => GetMemoriesAsync(Address.UnitScheme, id, kind, topicId, limit, offset,
            directoryService, memoryStore, cancellationToken);

    private static Task<IResult> GetAgentMemoriesAsync(
        string id,
        [FromQuery] string? kind,
        [FromQuery] Guid? topicId,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IMemoryStore memoryStore,
        CancellationToken cancellationToken)
        => GetMemoriesAsync(Address.AgentScheme, id, kind, topicId, limit, offset,
            directoryService, memoryStore, cancellationToken);

    private static Task<IResult> GetUnitTopicsAsync(
        string id,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IMemoryTopicStore topicStore,
        CancellationToken cancellationToken)
        => GetTopicsAsync(Address.UnitScheme, id, limit, offset,
            directoryService, topicStore, cancellationToken);

    private static Task<IResult> GetAgentTopicsAsync(
        string id,
        [FromQuery] int? limit,
        [FromQuery] int? offset,
        [FromServices] IDirectoryService directoryService,
        [FromServices] IMemoryTopicStore topicStore,
        CancellationToken cancellationToken)
        => GetTopicsAsync(Address.AgentScheme, id, limit, offset,
            directoryService, topicStore, cancellationToken);

    private static async Task<IResult> GetMemoriesAsync(
        string scheme,
        string id,
        string? kindArg,
        Guid? topicId,
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

        var kindFilter = ParseKindArg(kindArg);
        if (kindArg is not null && kindFilter is null)
        {
            return Results.Problem(
                detail: $"Query 'kind' must be '{KindLongTerm}' or '{KindShortTerm}'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var (limit, offset) = NormalisePaging(limitArg, offsetArg);

        var entries = await memoryStore.ListAsync(
            address, kindFilter, topicId, limit, offset, cancellationToken);

        // Partition into short/long-term arrays in-memory. The list
        // already filtered down to the requested kind if one was
        // supplied, so the other axis surfaces as empty — the wire
        // shape stays stable.
        var shortTerm = new List<WireMemoryEntry>();
        var longTerm = new List<WireMemoryEntry>();
        foreach (var row in entries)
        {
            var wire = ToWire(row);
            if (row.Kind == MemoryKind.ShortTerm)
            {
                shortTerm.Add(wire);
            }
            else
            {
                longTerm.Add(wire);
            }
        }

        return Results.Ok(new MemoriesResponse(shortTerm, longTerm));
    }

    private static async Task<IResult> GetTopicsAsync(
        string scheme,
        string id,
        int? limitArg,
        int? offsetArg,
        IDirectoryService directoryService,
        IMemoryTopicStore topicStore,
        CancellationToken cancellationToken)
    {
        var address = Address.For(scheme, id);
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        if (entry is null)
        {
            return NotFoundFor(scheme, id);
        }

        var (limit, offset) = NormalisePaging(limitArg, offsetArg);
        var topics = await topicStore.ListAsync(address, limit, offset, cancellationToken);
        return Results.Ok(new TopicsResponse(topics.Select(ToWire).ToList()));
    }

    private static IResult NotFoundFor(string scheme, string id) =>
        Results.Problem(
            detail: $"{char.ToUpperInvariant(scheme[0])}{scheme[1..]} '{id}' not found",
            statusCode: StatusCodes.Status404NotFound);

    private static MemoryKind? ParseKindArg(string? kindArg)
    {
        if (string.IsNullOrEmpty(kindArg))
        {
            return null;
        }
        if (string.Equals(kindArg, KindLongTerm, StringComparison.Ordinal))
        {
            return MemoryKind.LongTerm;
        }
        if (string.Equals(kindArg, KindShortTerm, StringComparison.Ordinal))
        {
            return MemoryKind.ShortTerm;
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
            Kind: entry.Kind == MemoryKind.ShortTerm ? KindShortTerm : KindLongTerm,
            UpdatedAt: entry.UpdatedAt,
            TopicIds: entry.TopicIds.Select(GuidFormatter.Format).ToList(),
            ThreadId: entry.ThreadId is { } tid ? GuidFormatter.Format(tid) : null);

    private static WireMemoryTopic ToWire(CoreMemoryTopic topic) =>
        new(
            Id: GuidFormatter.Format(topic.Id),
            Name: topic.Name,
            Description: topic.Description,
            CreatedAt: topic.CreatedAt,
            UpdatedAt: topic.UpdatedAt);
}
