// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Configuration;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Options;

/// <summary>
/// Tier-1 requirement: the agent-reachable API-host base URL
/// (<c>CallbackBaseUrl:BaseUrl</c>) that
/// <see cref="DispatcherCallbackEnvironmentBuilder"/> stamps onto every
/// runtime container as <c>SPRING_CALLBACK_URL</c>.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0054 retired the messaging callback surface; the value now backs the
/// OTLP-ingest plane — <c>LauncherOtelEnvironment</c> derives the
/// <c>/otlp</c> ingest endpoint from <c>SPRING_CALLBACK_URL</c>.
/// Without this requirement the value is validated lazily —
/// <see cref="DispatcherCallbackEnvironmentBuilder.AddCallbackEnvironment"/>
/// throws a <c>SpringException</c> only when it builds an agent's callback
/// environment, i.e. at the first runtime launch, minutes after host start.
/// Surfacing the misconfiguration at boot beats failing the first launch
/// (issue #2597).
/// </para>
/// <para>
/// The <c>IsMandatory</c> flag is set at registration time via
/// <see cref="CallbackBaseUrlConfigurationRequirementOptions"/>:
/// hosts that drive delegated execution register with <c>IsMandatory =
/// true</c>; design-time tooling skips the validator entirely (the
/// requirement is registered only when <c>!isDocGen</c>).
/// </para>
/// <para>
/// <b>Status mapping.</b>
/// </para>
/// <list type="bullet">
///   <item>Missing <c>BaseUrl</c> on a mandatory host → <see cref="ConfigurationStatus.Invalid"/> with a fatal error; aborts host startup.</item>
///   <item>Missing <c>BaseUrl</c> on a non-mandatory host → <see cref="ConfigurationStatus.Disabled"/> with a pointer at <c>CallbackBaseUrl:BaseUrl</c>.</item>
///   <item>Malformed <c>BaseUrl</c> (not a valid absolute HTTP(S) URI) → <see cref="ConfigurationStatus.Invalid"/>.</item>
///   <item>Valid <c>BaseUrl</c> → <see cref="ConfigurationStatus.Met"/>.</item>
/// </list>
/// </remarks>
public sealed class CallbackBaseUrlConfigurationRequirement(
    IOptions<CallbackBaseUrlOptions> optionsAccessor,
    CallbackBaseUrlConfigurationRequirementOptions registrationOptions) : IConfigurationRequirement
{
    private readonly IOptions<CallbackBaseUrlOptions> _options =
        optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
    private readonly CallbackBaseUrlConfigurationRequirementOptions _registrationOptions =
        registrationOptions ?? throw new ArgumentNullException(nameof(registrationOptions));

    /// <inheritdoc />
    public string RequirementId => "callback-base-url";

    /// <inheritdoc />
    public string DisplayName => "Callback base URL";

    /// <inheritdoc />
    public string SubsystemName => "Callback base URL";

    /// <inheritdoc />
    public bool IsMandatory => _registrationOptions.IsMandatory;

    /// <inheritdoc />
    public IReadOnlyList<string> EnvironmentVariableNames { get; } =
        new[] { "CallbackBaseUrl__BaseUrl" };

    /// <inheritdoc />
    public string? ConfigurationSectionPath =>
        CallbackBaseUrlOptions.SectionName + ":BaseUrl";

    /// <inheritdoc />
    public string Description =>
        "Agent-reachable base URL of the API host. The agent-runtime launcher "
        + "stamps it onto every runtime container as SPRING_CALLBACK_URL, from which the OTLP-ingest "
        + "endpoint is derived — a missing or malformed value "
        + "fails the first runtime launch rather than host startup unless validated here (issue #2597).";

    /// <inheritdoc />
    public Uri? DocumentationUrl { get; } =
        new Uri("https://github.com/cvoya-com/spring-voyage/blob/main/docs/architecture/deployment.md", UriKind.Absolute);

    /// <inheritdoc />
    public Task<ConfigurationRequirementStatus> ValidateAsync(CancellationToken cancellationToken)
    {
        var baseUrl = _options.Value.BaseUrl;

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            const string Suggestion =
                "Set CallbackBaseUrl:BaseUrl (environment variable CallbackBaseUrl__BaseUrl=...) "
                + "to the API host's agent-reachable base URL "
                + "(e.g. http://spring-caddy:8443/ on the spring-tenant-default network).";

            if (_registrationOptions.IsMandatory)
            {
                const string Reason =
                    "CallbackBaseUrl:BaseUrl is not set. The agent-runtime launcher stamps it onto "
                    + "every runtime container as SPRING_CALLBACK_URL and the first runtime launch "
                    + "would fail without it.";
                return Task.FromResult(ConfigurationRequirementStatus.Invalid(
                    reason: Reason,
                    suggestion: Suggestion,
                    fatalError: new InvalidOperationException(Reason + " " + Suggestion)));
            }

            return Task.FromResult(ConfigurationRequirementStatus.Disabled(
                reason: "CallbackBaseUrl:BaseUrl is not set — agent runtimes launched on this host "
                    + "cannot reach the API host's OTLP-ingest endpoint.",
                suggestion: Suggestion));
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            var reason = $"CallbackBaseUrl:BaseUrl '{baseUrl}' is not a valid absolute http(s) URI.";
            var suggestion =
                "Provide an absolute URL such as http://spring-caddy:8443/ "
                + "(the API host's agent-reachable base URL on the spring-tenant-default network).";
            return Task.FromResult(ConfigurationRequirementStatus.Invalid(
                reason: reason,
                suggestion: suggestion,
                fatalError: new InvalidOperationException(reason + " " + suggestion)));
        }

        return Task.FromResult(ConfigurationRequirementStatus.Met());
    }
}

/// <summary>
/// Registration-time options for
/// <see cref="CallbackBaseUrlConfigurationRequirement"/>. Registered
/// as a singleton by the DI extension that wires the requirement so the
/// requirement can read its <c>IsMandatory</c> value from a normal DI
/// constructor parameter (mirrors
/// <see cref="DispatcherConfigurationRequirementOptions"/>).
/// </summary>
/// <param name="IsMandatory">
/// When <c>true</c>, a missing or malformed <c>CallbackBaseUrl:BaseUrl</c>
/// aborts host startup. Hosts that launch agent runtimes register with
/// <c>true</c>; harnesses that exercise the DI graph without delegated
/// execution may register with <c>false</c>.
/// </param>
public sealed record CallbackBaseUrlConfigurationRequirementOptions(bool IsMandatory);
