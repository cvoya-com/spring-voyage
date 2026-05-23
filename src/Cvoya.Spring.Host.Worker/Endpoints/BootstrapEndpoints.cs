// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Worker.Endpoints;

using System.Net.Http.Headers;
using System.Text.Json;

using Cvoya.Spring.Core.Execution;

using Microsoft.AspNetCore.Mvc;

/// <summary>
/// Worker-hosted bootstrap endpoint (ADR-0055). The agent-sidecar inside
/// every agent container pulls its workspace files from
/// <c>GET /v1/bootstrap/agents/{agentId}</c> on launch and re-checks them
/// on every turn via the integrity check (ADR-0055 §6).
/// </summary>
/// <remarks>
/// <para>
/// Mounted on the worker's dedicated MCP-port Kestrel endpoint (alongside
/// <c>POST /mcp/</c>) so the bootstrap surface is reachable on the same
/// network plane the agent already uses for <c>SPRING_MCP_URL</c> and is
/// segregated from the Dapr app channel (ADR-0054 §2). The handler
/// rejects requests that arrive on the Dapr port with 404.
/// </para>
/// <para>
/// Auth is a per-agent bearer issued by <see cref="IAgentBootstrapAuthStore"/>
/// at agent provision time and revoked at undeploy (ADR-0055 §8). A token
/// presented for the wrong agentId is rejected with 401.
/// </para>
/// </remarks>
public static class BootstrapEndpoints
{
    /// <summary>
    /// Wire-format ETag string for an <see cref="AgentBootstrapBundle"/> —
    /// the bundle's content hash wrapped in double quotes per RFC 7232.
    /// </summary>
    internal static string FormatETag(string version) => $"\"{version}\"";

    private static readonly JsonSerializerOptions BundleJsonOptions = new(JsonSerializerDefaults.Web)
    {
        // Keep output compact — the bundle is delivered as the wire-form
        // contract the sidecar consumes, not a human-debugging surface.
        WriteIndented = false,
    };

    /// <summary>
    /// Maps <c>GET /v1/bootstrap/agents/{agentId}</c> onto <paramref name="app"/>.
    /// Pass <paramref name="restrictToLocalPort"/> when the route must reject
    /// requests that arrive on any other Kestrel endpoint — the worker passes
    /// the MCP port so the bootstrap surface stays segregated from the Dapr
    /// app channel.
    /// </summary>
    public static IEndpointRouteBuilder MapBootstrapEndpoints(
        this IEndpointRouteBuilder app,
        int? restrictToLocalPort = null)
    {
        app.MapGet("/v1/bootstrap/agents/{agentId}",
            async (HttpContext httpContext,
                string agentId,
                [FromServices] IAgentBootstrapAuthStore authStore,
                [FromServices] IAgentBootstrapBundleProvider bundleProvider,
                CancellationToken ct) =>
            {
                if (restrictToLocalPort is int port && httpContext.Connection.LocalPort != port)
                {
                    httpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await HandleAsync(httpContext, agentId, authStore, bundleProvider, ct);
            });

        return app;
    }

    /// <summary>
    /// Handles one bootstrap request. Public so tests can drive it with a
    /// hand-built <see cref="HttpContext"/> without standing up a Kestrel
    /// host. The route mapping above is a thin wrapper around this method.
    /// </summary>
    public static async Task HandleAsync(
        HttpContext httpContext,
        string agentId,
        IAgentBootstrapAuthStore authStore,
        IAgentBootstrapBundleProvider bundleProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(authStore);
        ArgumentNullException.ThrowIfNull(bundleProvider);

        var response = httpContext.Response;

        if (string.IsNullOrWhiteSpace(agentId))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var token = ExtractBearerToken(httpContext.Request.Headers.Authorization);
        if (token is null || !authStore.Validate(agentId, token))
        {
            // No body — bootstrap auth is opaque to the agent and to any
            // intermediary. 401 with no detail mirrors the MCP server's
            // ExtractBearerToken behaviour and keeps token-presence probes
            // from learning anything beyond "you got the auth wrong".
            response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var bundle = await bundleProvider.BuildAsync(agentId, cancellationToken);
        if (bundle is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var etag = FormatETag(bundle.Version);

        if (TryGetIfNoneMatch(httpContext.Request.Headers, out var inbound)
            && string.Equals(inbound, etag, StringComparison.Ordinal))
        {
            response.Headers.ETag = etag;
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers.ETag = etag;
        response.Headers.CacheControl = "no-cache";

        var payload = JsonSerializer.SerializeToUtf8Bytes(bundle, BundleJsonOptions);
        response.ContentLength = payload.Length;
        await response.Body.WriteAsync(payload, cancellationToken);
    }

    private static string? ExtractBearerToken(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }

        if (!AuthenticationHeaderValue.TryParse(authorizationHeader, out var parsed))
        {
            return null;
        }

        if (!string.Equals(parsed.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(parsed.Parameter))
        {
            return null;
        }

        return parsed.Parameter.Trim();
    }

    private static bool TryGetIfNoneMatch(IHeaderDictionary headers, out string? value)
    {
        if (headers.TryGetValue("If-None-Match", out var raw) && raw.Count > 0)
        {
            // RFC 7232 allows multiple comma-separated etags; ADR-0055's
            // contract is one bundle per agent so we accept only the
            // first entry. Trim whitespace; preserve the surrounding quotes.
            value = raw[0]?.Trim();
            return !string.IsNullOrEmpty(value);
        }
        value = null;
        return false;
    }
}
