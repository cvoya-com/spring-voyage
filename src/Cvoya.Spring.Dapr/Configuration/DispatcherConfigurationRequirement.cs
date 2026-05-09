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
/// Tier-1 requirement: the <c>spring-dispatcher</c> HTTP endpoint
/// (<c>Dispatcher:BaseUrl</c> / <c>Dispatcher:BearerToken</c>) used by
/// <see cref="DispatcherClientContainerRuntime"/> to launch and manage
/// delegated-execution containers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mandatory flag is <c>false</c>.</b> Both the API and Worker hosts
/// register the dispatcher client, but only the Worker actually invokes it
/// (agent deploy, workflow-orchestration strategy). An API-only deployment
/// with no dispatcher configured is a valid, expected topology — we report
/// Disabled instead of aborting boot, and the API host never drives
/// delegated execution so there is no "first call" to fail. On the Worker
/// host the dispatcher-dependent features surface their own errors when the
/// endpoint is missing at first call.
/// </para>
/// <para>
/// Replaces the silent "fail at first use" throw that used to live inside
/// <see cref="DispatcherClientContainerRuntime"/>: a blank
/// <c>Dispatcher:BaseUrl</c> would deferred the error to the first
/// <c>POST /v1/containers</c> call, many minutes after host start.
/// </para>
/// <para>
/// <b>Status mapping.</b>
/// </para>
/// <list type="bullet">
///   <item>Missing <c>BaseUrl</c> → <see cref="ConfigurationStatus.Disabled"/> with a pointer at <c>Dispatcher:BaseUrl</c>.</item>
///   <item>Malformed <c>BaseUrl</c> (not a valid absolute HTTP(S) URI) → <see cref="ConfigurationStatus.Invalid"/>.</item>
///   <item>Valid <c>BaseUrl</c> but empty <c>BearerToken</c> → <see cref="ConfigurationStatus.Met"/> with <see cref="SeverityLevel.Warning"/> — the dispatcher will reject unauthorised requests at deploy time.</item>
///   <item>Valid <c>BaseUrl</c> and <c>BearerToken</c> → <see cref="ConfigurationStatus.Met"/>.</item>
/// </list>
/// </remarks>
public sealed class DispatcherConfigurationRequirement(
    IOptions<DispatcherClientOptions> optionsAccessor) : IConfigurationRequirement
{
    /// <inheritdoc />
    public string RequirementId => "dispatcher-endpoint";

    /// <inheritdoc />
    public string DisplayName => "Dispatcher endpoint";

    /// <inheritdoc />
    public string SubsystemName => "Dispatcher";

    /// <inheritdoc />
    public bool IsMandatory => false;

    /// <inheritdoc />
    public IReadOnlyList<string> EnvironmentVariableNames { get; } =
        new[] { "Dispatcher__BaseUrl", "Dispatcher__BearerToken" };

    /// <inheritdoc />
    public string? ConfigurationSectionPath => DispatcherClientOptions.SectionName;

    /// <inheritdoc />
    public string Description =>
        "HTTP endpoint of the spring-dispatcher service used by the Worker host to launch delegated-execution containers. Optional — hosts that never drive delegated execution (e.g. the OSS API host on its own) leave it unset.";

    /// <inheritdoc />
    public Uri? DocumentationUrl { get; } =
        new Uri("https://github.com/cvoya-com/spring-voyage/blob/main/docs/architecture/deployment.md", UriKind.Absolute);

    /// <inheritdoc />
    public Task<ConfigurationRequirementStatus> ValidateAsync(CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return Task.FromResult(ConfigurationRequirementStatus.Disabled(
                reason: "Dispatcher:BaseUrl is not set — delegated-execution features (agent deploy, workflow orchestration) are unavailable on this host.",
                suggestion:
                    "Leave unset on hosts that never drive delegated execution (e.g. an OSS API host running on its own). " +
                    "On the Worker host, set Dispatcher:BaseUrl (environment variable Dispatcher__BaseUrl=...) to the spring-dispatcher HTTP endpoint " +
                    "(e.g. http://host.containers.internal:8090/ — the dispatcher runs on the host, not in a container; see issue #1063)."));
        }

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            var reason = $"Dispatcher:BaseUrl '{options.BaseUrl}' is not a valid absolute http(s) URI.";
            var suggestion =
                "Provide an absolute URL such as http://host.containers.internal:8090/ (Podman) or http://host.docker.internal:8090/ (Docker).";
            return Task.FromResult(ConfigurationRequirementStatus.Invalid(
                reason: reason,
                suggestion: suggestion,
                fatalError: new InvalidOperationException(reason + " " + suggestion)));
        }

        if (string.IsNullOrWhiteSpace(options.BearerToken))
        {
            return Task.FromResult(ConfigurationRequirementStatus.MetWithWarning(
                reason: "Dispatcher:BaseUrl is set but Dispatcher:BearerToken is empty.",
                suggestion:
                    "Set Dispatcher:BearerToken to the token issued for this worker at deploy time. " +
                    "The dispatcher will reject unauthenticated requests when the feature is exercised."));
        }

        return Task.FromResult(ConfigurationRequirementStatus.Met());
    }
}
