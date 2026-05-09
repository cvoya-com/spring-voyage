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
/// Tier-1 requirement: the container runtime type
/// (<c>ContainerRuntime:RuntimeType</c>) used by
/// <see cref="ContainerLifecycleManager"/> and <see cref="DaprSidecarManager"/>
/// for Dapr-sidecar management and per-user network plumbing on the
/// dispatcher host.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mandatory flag is <c>false</c>.</b> The OSS Worker no longer owns the
/// host container binary (ADR 0012; the <c>spring-dispatcher</c> service
/// does), so most deployments run the worker with the default podman binding
/// and never exercise this path. A malformed <c>RuntimeType</c> still
/// matters for the dispatcher host and for pre-split test harnesses; we
/// report it without aborting boot so non-dispatcher hosts continue to
/// start.
/// </para>
/// <para>
/// <b>Status mapping.</b>
/// </para>
/// <list type="bullet">
///   <item>Supported value (<c>podman</c> or <c>docker</c>, case-insensitive) → <see cref="ConfigurationStatus.Met"/>.</item>
///   <item>Empty / whitespace → <see cref="ConfigurationStatus.Invalid"/>.</item>
///   <item>Other value → <see cref="ConfigurationStatus.Invalid"/> with a suggestion.</item>
/// </list>
/// </remarks>
public sealed class ContainerRuntimeConfigurationRequirement(
    IOptions<ContainerRuntimeOptions> optionsAccessor) : IConfigurationRequirement
{
    private static readonly string[] SupportedRuntimes = { "podman", "docker" };

    /// <inheritdoc />
    public string RequirementId => "container-runtime-type";

    /// <inheritdoc />
    public string DisplayName => "Container runtime type";

    /// <inheritdoc />
    public string SubsystemName => "Container Runtime";

    /// <inheritdoc />
    public bool IsMandatory => false;

    /// <inheritdoc />
    public IReadOnlyList<string> EnvironmentVariableNames { get; } =
        new[] { "ContainerRuntime__RuntimeType" };

    /// <inheritdoc />
    public string? ConfigurationSectionPath => "ContainerRuntime";

    /// <inheritdoc />
    public string Description =>
        "Container runtime binary (podman or docker) used by the dispatcher host for sidecar and network lifecycle operations. The OSS Worker no longer launches containers directly — delegated execution goes through spring-dispatcher (ADR 0012).";

    /// <inheritdoc />
    public Uri? DocumentationUrl { get; } =
        new Uri("https://github.com/cvoya-com/spring-voyage/blob/main/docs/architecture/deployment.md", UriKind.Absolute);

    /// <inheritdoc />
    public Task<ConfigurationRequirementStatus> ValidateAsync(CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;
        var runtime = options.RuntimeType;

        if (string.IsNullOrWhiteSpace(runtime))
        {
            return Task.FromResult(ConfigurationRequirementStatus.Invalid(
                reason: "ContainerRuntime:RuntimeType is empty.",
                suggestion:
                    "Set ContainerRuntime:RuntimeType to 'podman' (OSS default) or 'docker'."));
        }

        foreach (var supported in SupportedRuntimes)
        {
            if (string.Equals(runtime, supported, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(ConfigurationRequirementStatus.Met());
            }
        }

        return Task.FromResult(ConfigurationRequirementStatus.Invalid(
            reason: $"ContainerRuntime:RuntimeType '{runtime}' is not a supported value.",
            suggestion: $"Supported values are: {string.Join(", ", SupportedRuntimes)} (case-insensitive). The OSS default is 'podman'."));
    }
}
