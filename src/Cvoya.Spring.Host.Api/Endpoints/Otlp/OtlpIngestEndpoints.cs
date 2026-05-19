// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints.Otlp;

using System.Globalization;
using System.Net.Mime;
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
/// OTLP/HTTP ingest endpoints (issue #2492 / #2501). Mounted under
/// <c>/otlp/v1/</c> and gated by the
/// <see cref="Auth.AuthConstants.OtlpCallbackScheme"/> auth scheme that
/// validates the per-invocation callback JWT the launcher injects into
/// the runtime container.
/// </summary>
/// <remarks>
/// <para>
/// The endpoints accept both <c>application/json</c> (OTLP/HTTP+JSON)
/// and <c>application/x-protobuf</c> (OTLP/HTTP+protobuf, issue #2501).
/// Runtimes that pin <c>OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf</c>
/// take the lean wire form; runtimes that pin <c>http/json</c> stay on
/// the JSON path. Unknown content types are rejected with the standard
/// <c>415 Unsupported Media Type</c>.
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

        // OTLP-style endpoints; both JSON and protobuf wire bodies are
        // accepted. OpenAPI metadata is intentionally minimal — these
        // aren't part of the tenant API surface that CLI / portal callers
        // consume.
        group.MapPost("/logs", IngestLogsAsync)
            .WithName("OtlpIngestLogs")
            .Accepts<OtlpLogsRequest>("application/json", OtlpProtobufDecoder.ContentType)
            .Produces<OtlpAcceptedResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status415UnsupportedMediaType)
            .ExcludeFromDescription();

        group.MapPost("/traces", IngestTracesAsync)
            .WithName("OtlpIngestTraces")
            .Accepts<OtlpTracesRequest>("application/json", OtlpProtobufDecoder.ContentType)
            .Produces<OtlpAcceptedResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status415UnsupportedMediaType)
            .ExcludeFromDescription();

        return group;
    }

    private static async Task<IResult> IngestLogsAsync(
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

        var (request, decodeError) = await ReadLogsRequestAsync(httpContext, cancellationToken);
        if (decodeError is not null)
        {
            return decodeError;
        }

        var events = OtlpEventMapper.MapLogs(request!, tenantId, subjectAddress, logger);
        await ingestService.IngestAsync(events, cancellationToken);
        return Results.Ok(new OtlpAcceptedResponse());
    }

    private static async Task<IResult> IngestTracesAsync(
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

        var (request, decodeError) = await ReadTracesRequestAsync(httpContext, cancellationToken);
        if (decodeError is not null)
        {
            return decodeError;
        }

        var events = OtlpEventMapper.MapTraces(request!, tenantId, subjectAddress, logger);
        await ingestService.IngestAsync(events, cancellationToken);
        return Results.Ok(new OtlpAcceptedResponse());
    }

    private static async Task<(OtlpLogsRequest? Request, IResult? Error)> ReadLogsRequestAsync(
        HttpContext httpContext, CancellationToken ct)
    {
        var contentType = httpContext.Request.ContentType ?? string.Empty;
        if (IsProtobufContentType(contentType))
        {
            var bytes = await ReadBodyAsync(httpContext, ct);
            try
            {
                return (OtlpProtobufDecoder.DecodeLogs(bytes), null);
            }
            catch (Exception)
            {
                return (null, Results.StatusCode(StatusCodes.Status400BadRequest));
            }
        }
        if (IsJsonContentType(contentType))
        {
            var parsed = await JsonSerializer.DeserializeAsync<OtlpLogsRequest>(
                httpContext.Request.Body,
                JsonOptions,
                ct);
            return (parsed ?? new OtlpLogsRequest(), null);
        }
        return (null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
    }

    private static async Task<(OtlpTracesRequest? Request, IResult? Error)> ReadTracesRequestAsync(
        HttpContext httpContext, CancellationToken ct)
    {
        var contentType = httpContext.Request.ContentType ?? string.Empty;
        if (IsProtobufContentType(contentType))
        {
            var bytes = await ReadBodyAsync(httpContext, ct);
            try
            {
                return (OtlpProtobufDecoder.DecodeTraces(bytes), null);
            }
            catch (Exception)
            {
                return (null, Results.StatusCode(StatusCodes.Status400BadRequest));
            }
        }
        if (IsJsonContentType(contentType))
        {
            var parsed = await JsonSerializer.DeserializeAsync<OtlpTracesRequest>(
                httpContext.Request.Body,
                JsonOptions,
                ct);
            return (parsed ?? new OtlpTracesRequest(), null);
        }
        return (null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext httpContext, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await httpContext.Request.Body.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static bool IsJsonContentType(string contentType)
        => contentType.StartsWith(MediaTypeNames.Application.Json, StringComparison.OrdinalIgnoreCase);

    private static bool IsProtobufContentType(string contentType)
        => contentType.StartsWith(OtlpProtobufDecoder.ContentType, StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
