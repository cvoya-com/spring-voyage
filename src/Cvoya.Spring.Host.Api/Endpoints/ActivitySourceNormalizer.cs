// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Resolves user-supplied activity <c>source</c> filter strings to the
/// canonical form persisted on <see cref="Cvoya.Spring.Core.Observability.ActivityEvent"/>
/// records — namely <c>{scheme}:{actorId}</c> for <c>unit:</c> and
/// <c>agent:</c> sources.
/// </summary>
/// <remarks>
/// <para>
/// The portal's tenant tree surfaces units and agents by slug, so the Activity
/// tab queries <c>/api/v1/activity?source=unit:&lt;slug&gt;</c>. The platform,
/// however, stores the event with the Dapr actor id as the source path
/// (<c>unit:&lt;uuid&gt;</c>), because emitters build their address from the
/// actor they live inside. Every slug-based query therefore returned an empty
/// result before this normalization ran (issue #987).
/// </para>
/// <para>
/// Callers feed the raw query-string value (either <c>scheme:path</c> or
/// <c>scheme://path</c>) into <see cref="NormalizeQuerySourceAsync"/> for the
/// REST query path, which rewrites slug paths to actor ids so the equality
/// filter on <c>ActivityEventRecord.Source</c> matches. For the SSE filter
/// envelope, which compares against <c>{scheme}://{actorId}</c>, use
/// <see cref="NormalizeStreamSourceAsync"/>.
/// </para>
/// <para>
/// Unknown slugs pass through unchanged (as <c>scheme:path</c> or
/// <c>scheme://path</c> respectively). The downstream query produces an
/// empty page, which matches the pre-fix behaviour shape — we never return
/// 400 for an unresolved source so legacy callers that emit a source from an
/// address that was later soft-deleted keep their "empty result" semantics.
/// </para>
/// </remarks>
public static class ActivitySourceNormalizer
{
    /// <summary>
    /// Normalizes a <c>source</c> query-string value for the REST
    /// <c>/api/v1/activity</c> endpoint. The persisted column format is
    /// <c>{scheme}:{actorId}</c>, so resolved sources come back with a single
    /// colon and the slug-or-uuid path replaced by the Dapr actor id.
    /// </summary>
    /// <param name="source">Raw <c>source</c> query-string value, e.g.
    /// <c>unit:portal-scratch-1</c>, <c>unit://portal-scratch-1</c>, or
    /// <c>agent:&lt;uuid&gt;</c>. May be <c>null</c> or empty.</param>
    /// <param name="directoryService">Directory service used to resolve the
    /// slug/uuid path to a <see cref="DirectoryEntry"/>.</param>
    /// <param name="cancellationToken">A token to cancel the resolve.</param>
    /// <returns>The normalized <c>source</c>, or the original value when
    /// normalization does not apply (non <c>unit:</c>/<c>agent:</c> scheme,
    /// empty input, or unresolved slug).</returns>
    public static async Task<string?> NormalizeQuerySourceAsync(
        string? source,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        if (!TrySplit(source, out var scheme, out var path))
        {
            return source;
        }

        if (!IsResolvableScheme(scheme))
        {
            return source;
        }

        var actorId = await ResolveActorIdAsync(scheme, path, directoryService, cancellationToken);
        if (actorId is null)
        {
            // Keep the original input verbatim so unknown slugs produce an
            // empty result page rather than a 400. Matches the pre-fix
            // observable shape for missing units/agents.
            return source;
        }

        return $"{scheme}:{actorId}";
    }

    /// <summary>
    /// Normalizes a <c>source</c> query-string value for the SSE
    /// <c>/api/v1/activity/stream</c> endpoint. The stream filter compares
    /// against <c>{scheme}://{path}</c>, so this helper emits the double-slash
    /// form with the slug rewritten to the actor id.
    /// </summary>
    /// <param name="source">Raw <c>source</c> query-string value. May be
    /// <c>null</c> or empty.</param>
    /// <param name="directoryService">Directory service used to resolve the
    /// slug/uuid path.</param>
    /// <param name="cancellationToken">A token to cancel the resolve.</param>
    /// <returns>The normalized <c>source</c>, or the original value when
    /// normalization does not apply.</returns>
    public static async Task<string?> NormalizeStreamSourceAsync(
        string? source,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        if (!TrySplit(source, out var scheme, out var path))
        {
            return source;
        }

        if (!IsResolvableScheme(scheme))
        {
            return source;
        }

        var actorId = await ResolveActorIdAsync(scheme, path, directoryService, cancellationToken);
        if (actorId is null)
        {
            return source;
        }

        return $"{scheme}://{actorId}";
    }

    private static bool TrySplit(string? source, out string scheme, out string path)
    {
        scheme = string.Empty;
        path = string.Empty;

        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var colonIndex = source.IndexOf(':');
        if (colonIndex <= 0 || colonIndex == source.Length - 1)
        {
            return false;
        }

        scheme = source[..colonIndex];
        var rest = source[(colonIndex + 1)..];

        // Accept both `scheme:path` (canonical persisted shape) and
        // `scheme://path` (URL-like; used by the stream filter and by
        // portal code that builds display strings with `://`).
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest[2..];
        }

        if (string.IsNullOrEmpty(rest))
        {
            return false;
        }

        path = rest;
        return true;
    }

    private static bool IsResolvableScheme(string scheme)
    {
        return string.Equals(scheme, "unit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, "agent", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> ResolveActorIdAsync(
        string scheme,
        string path,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var entry = await directoryService.ResolveAsync(new Address(scheme, path), cancellationToken);
        return entry?.ActorId;
    }
}