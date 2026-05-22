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
/// <b>Mandatory on every runtime host that drives delegated execution</b> —
/// that is, every runtime host other than design-time tooling such as the
/// build-time OpenAPI emitter. API-side call sites that drive delegated
/// execution include the imperative endpoints in <c>AgentEndpoints</c> /
/// <c>UnitEndpoints</c> (deploy, undeploy, runtime updates) that resolve
/// <see cref="PersistentAgentLifecycle"/>. The Worker (execution) host also
/// drives delegated execution and additionally runs
/// <see cref="PersistentAgentRegistry"/> as a hosted service — its
/// background restart path (the registry's health timer →
/// <see cref="PersistentAgentRegistry.TryRestartAsync"/>). Per ADR-0052 the
/// execution hosted services register only on the execution host
/// (<c>AddCvoyaSpringExecution</c> gates them by
/// <see cref="Cvoya.Spring.Dapr.DependencyInjection.SpringHostRole"/>), but
/// the dispatcher endpoint is still required on both hosts because the API
/// host resolves <see cref="PersistentAgentLifecycle"/> directly.
/// A missing <c>Dispatcher:BaseUrl</c> on either host previously deferred the
/// error to the first dispatcher call — minutes after host start — and on the
/// API host crashed the restart loop, after which the registry's catch
/// branch DELETEd the <c>persistent_agent_runtime</c> row (issue #2518).
/// </para>
/// <para>
/// The <c>IsMandatory</c> flag is set at registration time via
/// <see cref="DispatcherConfigurationRequirementOptions"/>: production hosts
/// register with <c>IsMandatory = true</c>; design-time tooling skips the
/// validator entirely (see <c>ServiceCollectionExtensions.Infrastructure</c>
/// — the requirement is registered only when <c>!isDocGen</c>).
/// </para>
/// <para>
/// <b>Status mapping.</b>
/// </para>
/// <list type="bullet">
///   <item>Missing <c>BaseUrl</c> on a mandatory host → <see cref="ConfigurationStatus.Invalid"/> with a fatal error; aborts host startup.</item>
///   <item>Missing <c>BaseUrl</c> on a non-mandatory host (test harness opting out) → <see cref="ConfigurationStatus.Disabled"/> with a pointer at <c>Dispatcher:BaseUrl</c>.</item>
///   <item>Malformed <c>BaseUrl</c> (not a valid absolute HTTP(S) URI) → <see cref="ConfigurationStatus.Invalid"/>.</item>
///   <item>Valid <c>BaseUrl</c> but empty <c>BearerToken</c> → <see cref="ConfigurationStatus.Met"/> with <see cref="SeverityLevel.Warning"/> — the dispatcher will reject unauthorised requests at deploy time.</item>
///   <item>Valid <c>BaseUrl</c> and <c>BearerToken</c> → <see cref="ConfigurationStatus.Met"/>.</item>
/// </list>
/// </remarks>
public sealed class DispatcherConfigurationRequirement(
    IOptions<DispatcherClientOptions> optionsAccessor,
    DispatcherConfigurationRequirementOptions registrationOptions) : IConfigurationRequirement
{
    /// <inheritdoc />
    public string RequirementId => "dispatcher-endpoint";

    /// <inheritdoc />
    public string DisplayName => "Dispatcher endpoint";

    /// <inheritdoc />
    public string SubsystemName => "Dispatcher";

    /// <inheritdoc />
    public bool IsMandatory => registrationOptions.IsMandatory;

    /// <inheritdoc />
    public IReadOnlyList<string> EnvironmentVariableNames { get; } =
        new[] { "Dispatcher__BaseUrl", "Dispatcher__BearerToken" };

    /// <inheritdoc />
    public string? ConfigurationSectionPath => DispatcherClientOptions.SectionName;

    /// <inheritdoc />
    public string Description =>
        "HTTP endpoint of the spring-dispatcher service used by every host that drives delegated execution (API + Worker) to launch and supervise delegated-execution containers.";

    /// <inheritdoc />
    public Uri? DocumentationUrl { get; } =
        new Uri("https://github.com/cvoya-com/spring-voyage/blob/main/docs/architecture/deployment.md", UriKind.Absolute);

    /// <inheritdoc />
    public Task<ConfigurationRequirementStatus> ValidateAsync(CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            const string Suggestion =
                "Set Dispatcher:BaseUrl (environment variable Dispatcher__BaseUrl=...) to the spring-dispatcher HTTP endpoint " +
                "(e.g. http://host.containers.internal:8090/ on Podman or http://host.docker.internal:8090/ on Docker — " +
                "the dispatcher runs as a host process, not in a container; see issue #1063).";

            if (registrationOptions.IsMandatory)
            {
                const string Reason =
                    "Dispatcher:BaseUrl is not set. This host drives delegated execution " +
                    "(deploy/undeploy endpoints, worker dispatch, and — on the execution host — the " +
                    "PersistentAgentRegistry restart loop) and requires the dispatcher endpoint.";
                return Task.FromResult(ConfigurationRequirementStatus.Invalid(
                    reason: Reason,
                    suggestion: Suggestion,
                    fatalError: new InvalidOperationException(Reason + " " + Suggestion)));
            }

            return Task.FromResult(ConfigurationRequirementStatus.Disabled(
                reason: "Dispatcher:BaseUrl is not set — delegated-execution features (agent deploy, workflow orchestration) are unavailable on this host.",
                suggestion: Suggestion));
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

/// <summary>
/// Registration-time options for <see cref="DispatcherConfigurationRequirement"/>.
/// Registered as a singleton by the DI extension that wires the requirement
/// so the requirement can read its <c>IsMandatory</c> value from a normal
/// DI constructor parameter (mirrors
/// <see cref="DatabaseConfigurationRequirement.TestHarnessSignal"/>).
/// </summary>
/// <param name="IsMandatory">
/// When <c>true</c>, a missing or malformed <c>Dispatcher:BaseUrl</c> aborts
/// host startup. Production hosts always register with <c>true</c>; test
/// harnesses that explicitly opt out of dispatcher-driven supervision (no
/// <c>PersistentAgentRegistry</c> hosted service) may register with
/// <c>false</c>.
/// </param>
public sealed record DispatcherConfigurationRequirementOptions(bool IsMandatory);
