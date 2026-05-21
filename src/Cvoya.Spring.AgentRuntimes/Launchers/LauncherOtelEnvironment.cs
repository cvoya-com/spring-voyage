// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Launchers;

using System.Globalization;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Adds OpenTelemetry / OTLP environment variables to the runtime
/// container so the in-container agent and SV Agent SDK can ship spans
/// and logs to the platform's <c>/otlp/v1/</c> ingest. Issue #2492.
/// </summary>
/// <remarks>
/// <para>
/// Builds the OTLP endpoint URL from the dispatcher runtime
/// callback base URL (<see cref="AgentCallbackEnvironmentContract.CallbackUrlEnvVar"/>),
/// which the launcher's existing
/// <see cref="LauncherCallbackEnvironment"/> already injected. The OTLP
/// endpoint sits at a sibling <c>/otlp/v1/</c> prefix on the same host;
/// the per-invocation callback token doubles as the OTLP auth token.
/// </para>
/// <para>
/// Resource attributes follow the issue body: <c>sv.subject.uuid</c>,
/// <c>sv.subject.kind</c>, <c>sv.tenant.id</c>. The ingest controller
/// cross-checks these against the bearer token's claims so a runtime
/// that mis-stamps attributes can't replay against another subject.
/// </para>
/// </remarks>
public static class LauncherOtelEnvironment
{
    /// <summary>
    /// Environment-variable name producers use to discover the OTLP
    /// HTTP endpoint. Standard OpenTelemetry naming.
    /// </summary>
    public const string OtlpEndpointEnvVar = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>OTLP wire protocol selector — <c>http/protobuf</c> or <c>http/json</c>.</summary>
    public const string OtlpProtocolEnvVar = "OTEL_EXPORTER_OTLP_PROTOCOL";

    /// <summary>
    /// Default OTLP wire protocol the launcher pins for new runtime
    /// spawns (issue #2501). Protobuf is leaner on the wire and is the
    /// OTel spec's recommendation; <c>http/json</c> is still accepted by
    /// the ingest controller for callers that prefer it.
    /// </summary>
    public const string DefaultOtlpProtocol = "http/protobuf";

    /// <summary>Per-OTLP bearer-token authentication header injection.</summary>
    public const string OtlpHeadersEnvVar = "OTEL_EXPORTER_OTLP_HEADERS";

    /// <summary>OTLP resource attributes (comma-separated key=value).</summary>
    public const string OtlpResourceAttributesEnvVar = "OTEL_RESOURCE_ATTRIBUTES";

    /// <summary>OTLP service name resource attribute (well-known OTel key).</summary>
    public const string OtlpServiceNameEnvVar = "OTEL_SERVICE_NAME";

    /// <summary>
    /// Path appended to the dispatcher callback base URL to produce the
    /// OTLP ingest endpoint. The OTLP spec recommends one base URL per
    /// signal-suffix path — producers compose <c>/v1/traces</c> and
    /// <c>/v1/logs</c> off this base internally.
    /// </summary>
    public const string OtlpRoutePrefix = "/otlp";

    /// <summary>
    /// Builds the OTLP-related env vars into <paramref name="envVars"/>.
    /// Idempotent — overwrites prior values so a future re-export with
    /// updated routing or token wins.
    /// </summary>
    public static void Add(AgentLaunchContext context, IDictionary<string, string> envVars)
    {
        if (!envVars.TryGetValue(AgentCallbackEnvironmentContract.CallbackUrlEnvVar, out var callbackBaseUrl)
            || string.IsNullOrEmpty(callbackBaseUrl))
        {
            // The callback-env builder is the only producer of the base
            // URL today. If it's missing, the launcher pipeline has
            // already failed loudly elsewhere — skip OTLP injection
            // silently so a regression in callback wiring doesn't take
            // down the runtime container.
            return;
        }

        if (!Uri.TryCreate(callbackBaseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new SpringException(
                $"{AgentCallbackEnvironmentContract.CallbackUrlEnvVar} must be an absolute URL for OTLP ingest wiring.");
        }

        // Rewrite the callback path to the sibling /otlp prefix.
        var otlpEndpoint = $"{baseUri.Scheme}://{baseUri.Authority}{OtlpRoutePrefix}";
        envVars[OtlpEndpointEnvVar] = otlpEndpoint;
        // #2501: default to OTLP/HTTP+protobuf for new runtime spawns.
        // The ingest controller still accepts http/json for SDKs that
        // pin the JSON form explicitly.
        envVars[OtlpProtocolEnvVar] = DefaultOtlpProtocol;

        if (envVars.TryGetValue(AgentCallbackEnvironmentContract.CallbackTokenEnvVar, out var callbackToken)
            && !string.IsNullOrEmpty(callbackToken))
        {
            envVars[OtlpHeadersEnvVar] = $"Authorization=Bearer {callbackToken}";
        }

        var subjectAddress = context.AgentAddress
            ?? new Address(Address.AgentScheme, ParseGuidOrEmpty(context.AgentId));
        var subjectKind = subjectAddress.Scheme;

        envVars[OtlpServiceNameEnvVar] = $"spring-voyage/{subjectKind}";

        var resourceAttributes = string.Format(
            CultureInfo.InvariantCulture,
            "sv.tenant.id={0},sv.subject.uuid={1},sv.subject.kind={2}",
            GuidFormatter.Format(context.TenantId),
            GuidFormatter.Format(subjectAddress.Id),
            subjectKind);
        envVars[OtlpResourceAttributesEnvVar] = resourceAttributes;
    }

    private static Guid ParseGuidOrEmpty(string raw)
        => GuidFormatter.TryParse(raw, out var id) ? id : Guid.Empty;
}
