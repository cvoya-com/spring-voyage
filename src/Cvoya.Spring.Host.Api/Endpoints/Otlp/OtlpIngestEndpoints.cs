// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints.Otlp;

using System.Globalization;
using System.Security.Claims;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Runtime;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

/// <summary>
/// OTLP/HTTP+JSON ingest endpoints (issue #2492). Mounted under
/// <c>/otlp/v1/</c> and gated by the
/// <see cref="Auth.AuthConstants.OtlpCallbackScheme"/> auth scheme that
/// validates the per-invocation callback JWT the launcher injects into
/// the runtime container.
/// </summary>
/// <remarks>
/// <para>
/// The endpoints accept the OTLP/HTTP+JSON wire format only. Protobuf
/// support is a follow-up — runtimes emit JSON natively when the
/// launcher sets <c>OTEL_EXPORTER_OTLP_PROTOCOL=http/json</c>.
/// </para>
/// <para>
/// Auth-claim cross-check: every event in the batch must declare a
/// <c>sv.tenant.id</c> resource attribute that matches the bearer
/// token's <c>sv_tid</c> claim, and a <c>sv.subject.uuid</c> that
/// matches the token's <c>sv_addr</c>. Events whose resource attributes
/// disagree with the token are dropped silently (counted in the
/// response's <c>droppedError</c>) — the ingest path is best-effort,
/// never blocking the A2A path even when a runtime mis-stamps resource
/// attributes.
/// </para>
/// </remarks>
public static class OtlpIngestEndpoints
{
    /// <summary>Maps the OTLP ingest endpoints onto <paramref name="app"/>.</summary>
    public static RouteGroupBuilder MapOtlpIngestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/otlp/v1")
            .WithTags("Otlp");

        // OTLP-style endpoints; the wire body is `application/json`.
        // OpenAPI metadata is intentionally minimal — these aren't part
        // of the tenant API surface that CLI / portal callers consume.
        group.MapPost("/logs", IngestLogsAsync)
            .WithName("OtlpIngestLogs")
            .Accepts<OtlpLogsRequest>("application/json")
            .Produces<OtlpAcceptedResponse>(StatusCodes.Status200OK)
            .ExcludeFromDescription();

        group.MapPost("/traces", IngestTracesAsync)
            .WithName("OtlpIngestTraces")
            .Accepts<OtlpTracesRequest>("application/json")
            .Produces<OtlpAcceptedResponse>(StatusCodes.Status200OK)
            .ExcludeFromDescription();

        return group;
    }

    private static async Task<IResult> IngestLogsAsync(
        [FromBody] OtlpLogsRequest request,
        HttpContext httpContext,
        IOtlpIngestService ingestService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.Otlp.OtlpIngestEndpoints");
        if (!TryAuthorize(httpContext, out var tenantId, out var subjectAddress))
        {
            // Auth scheme already responded; fail-closed without leaking detail.
            return Results.Unauthorized();
        }

        var events = OtlpEventMapper.MapLogs(request, tenantId, subjectAddress, logger);
        var result = await ingestService.IngestAsync(events, cancellationToken);
        return Results.Ok(new OtlpAcceptedResponse());
    }

    private static async Task<IResult> IngestTracesAsync(
        [FromBody] OtlpTracesRequest request,
        HttpContext httpContext,
        IOtlpIngestService ingestService,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.Otlp.OtlpIngestEndpoints");
        if (!TryAuthorize(httpContext, out var tenantId, out var subjectAddress))
        {
            return Results.Unauthorized();
        }

        var events = OtlpEventMapper.MapTraces(request, tenantId, subjectAddress, logger);
        var result = await ingestService.IngestAsync(events, cancellationToken);
        return Results.Ok(new OtlpAcceptedResponse());
    }

    private static bool TryAuthorize(
        HttpContext httpContext,
        out Guid tenantId,
        out Address subjectAddress)
    {
        tenantId = Guid.Empty;
        subjectAddress = default!;

        var user = httpContext.User;
        var tenantClaim = user.FindFirstValue(CallbackTokenClaimNames.TenantId);
        if (string.IsNullOrEmpty(tenantClaim) || !GuidFormatter.TryParse(tenantClaim, out tenantId))
        {
            return false;
        }

        var addressClaim = user.FindFirstValue(CallbackTokenClaimNames.AgentAddress);
        if (string.IsNullOrEmpty(addressClaim)
            || !Address.TryParse(addressClaim, out var parsed) || parsed is null)
        {
            return false;
        }

        subjectAddress = parsed;
        return true;
    }

    /// <summary>
    /// Helper exposed for tests: parses the OTel resource-attribute list
    /// for the canonical <c>sv.tenant.id</c> / <c>sv.subject.uuid</c> /
    /// <c>sv.subject.kind</c> values, returning <c>true</c> when they
    /// match the authenticated <paramref name="tenantId"/> /
    /// <paramref name="subjectAddress"/>.
    /// </summary>
    internal static bool ResourceMatchesPrincipal(
        OtlpResource? resource,
        Guid tenantId,
        Address subjectAddress)
    {
        if (resource is null)
        {
            return false;
        }

        Guid? declaredTenant = null;
        Guid? declaredSubject = null;
        string? declaredKind = null;
        foreach (var attr in resource.Attributes)
        {
            switch (attr.Key)
            {
                case OtelResourceKeys.TenantId when attr.Value?.StringValue is { } tStr
                    && GuidFormatter.TryParse(tStr, out var tGuid):
                    declaredTenant = tGuid;
                    break;
                case OtelResourceKeys.SubjectUuid when attr.Value?.StringValue is { } sStr
                    && GuidFormatter.TryParse(sStr, out var sGuid):
                    declaredSubject = sGuid;
                    break;
                case OtelResourceKeys.SubjectKind:
                    declaredKind = attr.Value?.StringValue;
                    break;
            }
        }

        if (declaredTenant != tenantId || declaredSubject != subjectAddress.Id)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(declaredKind)
            && !string.Equals(declaredKind, subjectAddress.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Helper exposed for tests: converts a Unix-nanos string to
    /// <see cref="DateTimeOffset"/>, falling back to the current time when
    /// parsing fails (best-effort ingest).
    /// </summary>
    internal static DateTimeOffset ParseUnixNanos(string? value, TimeProvider timeProvider)
    {
        if (string.IsNullOrEmpty(value) || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nanos))
        {
            return timeProvider.GetUtcNow();
        }
        // Unix nanoseconds → DateTimeOffset.
        var ticks = nanos / 100; // 100 ns per tick.
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(nanos / 1_000_000);
        return dt;
    }
}

/// <summary>Canonical Spring Voyage OTel resource-attribute keys (issue #2492).</summary>
internal static class OtelResourceKeys
{
    public const string TenantId = "sv.tenant.id";
    public const string SubjectUuid = "sv.subject.uuid";
    public const string SubjectKind = "sv.subject.kind";
    public const string ThreadId = "sv.thread.id";
    public const string MessageId = "sv.message.id";
}
