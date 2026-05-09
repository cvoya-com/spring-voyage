// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Configuration;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Dapr.State;

using Microsoft.Extensions.Options;

/// <summary>
/// Tier-1 requirement: the Dapr state store component name
/// (<c>DaprStateStore:StoreName</c>) used by <see cref="DaprStateStore"/> and
/// every platform subsystem that persists via the Dapr state building block
/// (initiative budgets, cloning policies, unit / agent actor state). Mandatory
/// — the platform cannot run without the shared state store component.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the silent default that used to live inside <see cref="DaprStateStore"/>:
/// a blank or missing <c>StoreName</c> would have quietly resolved to empty
/// string and every Dapr call returned "component not found" at first use.
/// The requirement surfaces the misconfiguration at host start.
/// </para>
/// <para>
/// The requirement only validates the <b>component-name string shape</b> — it
/// does not probe the Dapr sidecar for component availability. Sidecar
/// health is an orchestration concern (the sidecar may not be up yet when the
/// host starts; the shared Kubernetes readiness probe / docker-compose
/// <c>depends_on</c> wiring governs that).
/// </para>
/// </remarks>
public sealed class DaprStateStoreConfigurationRequirement(
    IOptions<DaprStateStoreOptions> optionsAccessor) : IConfigurationRequirement
{
    /// <inheritdoc />
    public string RequirementId => "dapr-state-store";

    /// <inheritdoc />
    public string DisplayName => "Dapr state store component";

    /// <inheritdoc />
    public string SubsystemName => "Dapr State Store";

    /// <inheritdoc />
    public bool IsMandatory => true;

    /// <inheritdoc />
    public IReadOnlyList<string> EnvironmentVariableNames { get; } =
        new[] { "DaprStateStore__StoreName" };

    /// <inheritdoc />
    public string? ConfigurationSectionPath => DaprStateStoreOptions.SectionName;

    /// <inheritdoc />
    public string Description =>
        "Name of the Dapr state-store component used for platform key-value persistence (initiative budgets, cloning policies, actor state). Must match a component declared in the Dapr sidecar's components directory.";

    /// <inheritdoc />
    public Uri? DocumentationUrl { get; } =
        new Uri("https://github.com/cvoya-com/spring-voyage/blob/main/docs/architecture/deployment.md", UriKind.Absolute);

    /// <inheritdoc />
    public Task<ConfigurationRequirementStatus> ValidateAsync(CancellationToken cancellationToken)
    {
        var options = optionsAccessor.Value;

        if (string.IsNullOrWhiteSpace(options.StoreName))
        {
            return Task.FromResult(ConfigurationRequirementStatus.Invalid(
                reason: "DaprStateStore:StoreName is empty.",
                suggestion:
                    "Set DaprStateStore:StoreName (environment variable DaprStateStore__StoreName=...) to the name of the " +
                    "Dapr state-store component declared in your components directory. The OSS default is \"statestore\".",
                fatalError: new InvalidOperationException(
                    "DaprStateStore:StoreName is empty. Set it to the Dapr state-store component name (e.g. \"statestore\").")));
        }

        return Task.FromResult(ConfigurationRequirementStatus.Met());
    }
}
