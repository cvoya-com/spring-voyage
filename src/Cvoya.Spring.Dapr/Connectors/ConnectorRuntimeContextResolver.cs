// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors;

using System.Globalization;
using System.Text;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IConnectorRuntimeContextResolver"/>.
/// </summary>
/// <remarks>
/// <para>
/// Walks direct + inherited bindings via the shared
/// <see cref="ConnectorBindingWalker"/> (#2442) — both this resolver
/// and the prompt-context resolver use the same walk so the two stay
/// in lockstep on what "the bindings that apply to a subject" means.
/// </para>
/// </remarks>
public class ConnectorRuntimeContextResolver(
    ConnectorBindingWalker bindingWalker,
    ITenantContext tenantContext,
    IEnumerable<IConnectorType> connectorTypes,
    IEnumerable<IConnectorRuntimeContextContributor> contributors,
    ILogger<ConnectorRuntimeContextResolver> logger) : IConnectorRuntimeContextResolver
{
    /// <summary>
    /// Reserved env-var prefix the contributor seam owns. Every key a
    /// contributor returns must begin with this prefix; the resolver
    /// fails fast on anything else.
    /// </summary>
    internal const string EnvVarPrefix = "SPRING_CONNECTOR_";

    /// <summary>
    /// Sub-path prefix every contributed context file must sit under.
    /// Mirrors the env-var namespace convention so connectors cannot
    /// shadow platform mounts (<c>agent-definition.yaml</c>,
    /// <c>tenant-config.json</c>) by accident.
    /// </summary>
    internal const string ContextFileDirectory = "connectors/";

    private readonly Dictionary<Guid, IConnectorType> _connectorTypesById =
        BuildConnectorTypeMap(connectorTypes);

    private readonly Dictionary<Guid, IConnectorRuntimeContextContributor> _contributorsByType =
        BuildContributorMap(contributors, logger);

    private static Dictionary<Guid, IConnectorType> BuildConnectorTypeMap(
        IEnumerable<IConnectorType> types)
    {
        var map = new Dictionary<Guid, IConnectorType>();
        foreach (var type in types)
        {
            map[type.TypeId] = type;
        }
        return map;
    }

    private static Dictionary<Guid, IConnectorRuntimeContextContributor> BuildContributorMap(
        IEnumerable<IConnectorRuntimeContextContributor> contributors,
        ILogger logger)
    {
        var map = new Dictionary<Guid, IConnectorRuntimeContextContributor>();
        foreach (var contributor in contributors)
        {
            if (map.ContainsKey(contributor.ConnectorTypeId))
            {
                logger.LogWarning(
                    "Multiple IConnectorRuntimeContextContributor implementations registered for type {TypeId}; " +
                    "keeping the first ({First}) and ignoring {Duplicate}.",
                    contributor.ConnectorTypeId,
                    map[contributor.ConnectorTypeId].GetType().Name,
                    contributor.GetType().Name);
                continue;
            }
            map[contributor.ConnectorTypeId] = contributor;
        }
        return map;
    }

    /// <inheritdoc />
    public async Task<ConnectorRuntimeContextContribution> ResolveAsync(
        Address subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        // No contributors registered → no work to do. (The walker also
        // returns early for non-agent / non-unit schemes; the local
        // short-circuit avoids the membership lookup when nobody cares.)
        if (_contributorsByType.Count == 0)
        {
            return ConnectorRuntimeContextContribution.Empty;
        }

        // Walk the subject's direct + inherited bindings via the shared
        // helper (#2442). For each unique connector type id the closest
        // unit's binding wins (specific over inherited).
        var bindings = await bindingWalker.WalkAsync(subject, cancellationToken);
        if (bindings.Count == 0)
        {
            return ConnectorRuntimeContextContribution.Empty;
        }

        var mergedEnv = new Dictionary<string, string>(StringComparer.Ordinal);
        var mergedFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        var envKeyOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var fileKeyOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var tenantId = tenantContext.CurrentTenantId;

        foreach (var entry in bindings)
        {
            if (!_contributorsByType.TryGetValue(entry.Binding.TypeId, out var contributor))
            {
                // A binding for a connector that ships no runtime contributor
                // is a perfectly legal configuration (most connectors don't
                // need one). Skip it without warning.
                continue;
            }

            var connectorType = _connectorTypesById.TryGetValue(entry.Binding.TypeId, out var ct)
                ? ct
                : null;
            var slug = connectorType?.Slug
                ?? throw new SpringException(
                    $"Connector type {entry.Binding.TypeId} declares a runtime contributor but no " +
                    "IConnectorType is registered under that id; cannot validate the env-var namespace.");

            var request = new ConnectorRuntimeContextRequest(
                Subject: subject,
                BindingOwnerUnitId: entry.OwnerUnitId,
                Binding: entry.Binding,
                TenantId: tenantId);

            ConnectorRuntimeContextContribution contribution;
            try
            {
                contribution = await contributor.ContributeAsync(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Per the resolver contract a contributor failure aborts
                // the launch — silently dropping its contribution would
                // leave the container with a partial environment.
                throw new SpringException(
                    $"Connector '{slug}' (type {entry.Binding.TypeId}) failed to contribute runtime context " +
                    $"for subject {subject} (owner unit {entry.OwnerUnitId:N}): {ex.Message}",
                    ex);
            }

            if (contribution is null || contribution == ConnectorRuntimeContextContribution.Empty)
            {
                continue;
            }

            var requiredEnvPrefix = BuildEnvPrefix(slug);

            foreach (var kvp in contribution.EnvironmentVariables)
            {
                if (!kvp.Key.StartsWith(requiredEnvPrefix, StringComparison.Ordinal))
                {
                    throw new SpringException(
                        $"Connector '{slug}' contributed env-var '{kvp.Key}' which violates the required " +
                        $"namespace '{requiredEnvPrefix}*'. Every IConnectorRuntimeContextContributor must scope " +
                        "its env vars under SPRING_CONNECTOR_<SLUG_UPPER>_* per the seam contract.");
                }

                if (envKeyOwners.TryGetValue(kvp.Key, out var previousOwner))
                {
                    throw new SpringException(
                        $"Connector '{slug}' contributed env-var '{kvp.Key}' which was already contributed by " +
                        $"connector '{previousOwner}'. Resolve the collision in DI registrations.");
                }

                envKeyOwners[kvp.Key] = slug;
                mergedEnv[kvp.Key] = kvp.Value;
            }

            // #2442: well-known aliases (e.g. GITHUB_TOKEN) intentionally
            // sit outside the SPRING_CONNECTOR_<SLUG>_* namespace — they
            // are convenience hops for ecosystem tooling (gh / git read
            // GITHUB_TOKEN natively). The no-collision rule still
            // applies; the namespace rule does not. A collision with a
            // platform-bootstrap name is caught at the dispatcher's
            // final merge step, same as for namespaced vars.
            if (contribution.WellKnownAliasEnvironmentVariables is { Count: > 0 } aliases)
            {
                foreach (var kvp in aliases)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key))
                    {
                        throw new SpringException(
                            $"Connector '{slug}' contributed a blank well-known alias env-var key.");
                    }

                    if (envKeyOwners.TryGetValue(kvp.Key, out var previousOwner))
                    {
                        throw new SpringException(
                            $"Connector '{slug}' contributed well-known alias env-var '{kvp.Key}' which was " +
                            $"already contributed by connector '{previousOwner}'. Resolve the collision in DI registrations.");
                    }

                    envKeyOwners[kvp.Key] = slug;
                    mergedEnv[kvp.Key] = kvp.Value;
                }
            }

            var requiredFilePrefix = BuildFilePrefix(slug);
            foreach (var kvp in contribution.ContextFiles)
            {
                if (!kvp.Key.StartsWith(requiredFilePrefix, StringComparison.Ordinal))
                {
                    throw new SpringException(
                        $"Connector '{slug}' contributed context file '{kvp.Key}' which violates the required " +
                        $"sub-path '{requiredFilePrefix}*'. Every IConnectorRuntimeContextContributor must scope " +
                        "its files under connectors/<slug>/* per the seam contract.");
                }

                if (fileKeyOwners.TryGetValue(kvp.Key, out var previousOwner))
                {
                    throw new SpringException(
                        $"Connector '{slug}' contributed context file '{kvp.Key}' which was already contributed " +
                        $"by connector '{previousOwner}'. Resolve the collision in DI registrations.");
                }

                fileKeyOwners[kvp.Key] = slug;
                mergedFiles[kvp.Key] = kvp.Value;
            }

            logger.LogInformation(
                "Connector '{Slug}' contributed {EnvCount} env-var(s) ({AliasCount} aliases) and " +
                "{FileCount} context file(s) for subject {Subject} from binding on unit {OwnerUnit:N}",
                slug, contribution.EnvironmentVariables.Count,
                contribution.WellKnownAliasEnvironmentVariables?.Count ?? 0,
                contribution.ContextFiles.Count, subject, entry.OwnerUnitId);
        }

        if (mergedEnv.Count == 0 && mergedFiles.Count == 0)
        {
            return ConnectorRuntimeContextContribution.Empty;
        }

        return new ConnectorRuntimeContextContribution(mergedEnv, mergedFiles);
    }

    /// <summary>
    /// Builds the required env-var prefix for a connector slug per the
    /// <see cref="IConnectorRuntimeContextContributor"/> contract:
    /// <c>SPRING_CONNECTOR_&lt;SLUG_UPPER&gt;_</c>. Non-alphanumeric
    /// characters are replaced by underscores so a connector slug like
    /// <c>my-connector</c> resolves to <c>SPRING_CONNECTOR_MY_CONNECTOR_</c>.
    /// </summary>
    internal static string BuildEnvPrefix(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        var builder = new StringBuilder(EnvVarPrefix.Length + slug.Length + 1);
        builder.Append(EnvVarPrefix);
        foreach (var c in slug)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
            else
            {
                builder.Append('_');
            }
        }
        builder.Append('_');
        return builder.ToString();
    }

    /// <summary>
    /// Builds the required context-file sub-path prefix for a connector
    /// slug. The slug is lower-cased and used verbatim (slugs are already
    /// URL-safe per <see cref="IConnectorType.Slug"/>'s contract).
    /// </summary>
    internal static string BuildFilePrefix(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}{1}/",
            ContextFileDirectory,
            slug.ToLowerInvariant());
    }

}
