// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors;

using System.Globalization;
using System.Text;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IConnectorRuntimeContextResolver"/>.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton over <see cref="IServiceScopeFactory"/> so it
/// can pull scoped collaborators (<see cref="IUnitMembershipRepository"/>)
/// and the connector type registry from a fresh per-call scope without
/// invading the dispatcher's singleton lifetime.
/// </para>
/// <para>
/// Walks the inheritance graph in two layers per #2380:
/// <list type="number">
///   <item><description>If the subject is an <c>agent:</c>, look up its
///   primary parent unit (first membership by <c>CreatedAt</c>). Continue
///   the walk from that unit.</description></item>
///   <item><description>Starting from the resolved unit, collect direct +
///   inherited bindings by walking ancestors via
///   <see cref="IUnitHierarchyResolver"/>. For each connector type id, the
///   closest unit's binding wins.</description></item>
/// </list>
/// </para>
/// </remarks>
public class ConnectorRuntimeContextResolver(
    IServiceScopeFactory scopeFactory,
    IUnitConnectorBindingStore bindingStore,
    IUnitHierarchyResolver hierarchyResolver,
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

        // The resolver only walks unit / agent dispatch targets — humans
        // and other addressables never receive a connector runtime context.
        if (!string.Equals(subject.Scheme, Address.AgentScheme, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(subject.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
        {
            return ConnectorRuntimeContextContribution.Empty;
        }

        // No contributors registered → no work to do.
        if (_contributorsByType.Count == 0)
        {
            return ConnectorRuntimeContextContribution.Empty;
        }

        var startingUnitId = await ResolveStartingUnitAsync(subject, cancellationToken);
        if (startingUnitId is null)
        {
            // An agent with no membership has no unit to walk from. The
            // dispatcher already handles the bare-agent case; a missing
            // membership means no inherited bindings, which is fine.
            return ConnectorRuntimeContextContribution.Empty;
        }

        // Walk the unit's parent chain. For each unique connector type id
        // we keep the binding from the closest unit (specific wins).
        var bindings = await CollectBindingsAsync(startingUnitId.Value, cancellationToken);
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
                "Connector '{Slug}' contributed {EnvCount} env-var(s) and {FileCount} context file(s) " +
                "for subject {Subject} from binding on unit {OwnerUnit:N}",
                slug, contribution.EnvironmentVariables.Count, contribution.ContextFiles.Count,
                subject, entry.OwnerUnitId);
        }

        if (mergedEnv.Count == 0 && mergedFiles.Count == 0)
        {
            return ConnectorRuntimeContextContribution.Empty;
        }

        return new ConnectorRuntimeContextContribution(mergedEnv, mergedFiles);
    }

    /// <summary>
    /// Resolves the unit the parent-chain walk should start from. For a
    /// <c>unit:</c> subject this is the unit itself; for an <c>agent:</c>
    /// subject this is the agent's primary parent unit (first membership
    /// by <c>CreatedAt</c>). An agent with no memberships returns
    /// <c>null</c>.
    /// </summary>
    private async Task<Guid?> ResolveStartingUnitAsync(Address subject, CancellationToken cancellationToken)
    {
        if (string.Equals(subject.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
        {
            return subject.Id;
        }

        // Agent subject — look up the primary parent unit through a fresh
        // scope (the membership repo is scoped per the EF DbContext lifetime).
        await using var scope = scopeFactory.CreateAsyncScope();
        var membershipRepo = scope.ServiceProvider.GetService<IUnitMembershipRepository>();
        if (membershipRepo is null)
        {
            return null;
        }

        var memberships = await membershipRepo.ListByAgentAsync(subject.Id, cancellationToken);
        return memberships.Count == 0 ? null : memberships[0].UnitId;
    }

    /// <summary>
    /// Walks the unit and its ancestors, returning the binding to use for
    /// each connector type id. A direct binding on the starting unit
    /// shadows any inherited binding of the same type.
    /// </summary>
    private async Task<IReadOnlyList<ResolvedBinding>> CollectBindingsAsync(
        Guid startingUnitId,
        CancellationToken cancellationToken)
    {
        const int MaxDepth = 32;

        var byType = new Dictionary<Guid, ResolvedBinding>();
        var queue = new Queue<(Guid UnitId, int Depth)>();
        var visited = new HashSet<Guid>();
        queue.Enqueue((startingUnitId, 0));

        while (queue.Count > 0)
        {
            var (currentUnitId, depth) = queue.Dequeue();
            if (depth > MaxDepth)
            {
                logger.LogWarning(
                    "Connector binding walk exceeded max depth {MaxDepth} starting from unit {Unit:N}; bailing out.",
                    MaxDepth, startingUnitId);
                break;
            }

            if (!visited.Add(currentUnitId))
            {
                continue;
            }

            var binding = await bindingStore.GetAsync(currentUnitId, cancellationToken);
            if (binding is not null && !byType.ContainsKey(binding.TypeId))
            {
                byType[binding.TypeId] = new ResolvedBinding(currentUnitId, binding);
            }

            var parents = await hierarchyResolver.GetParentsAsync(
                new Address(Address.UnitScheme, currentUnitId), cancellationToken);
            foreach (var parent in parents)
            {
                queue.Enqueue((parent.Id, depth + 1));
            }
        }

        return [.. byType.Values];
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

    /// <summary>One entry of the resolver's binding walk.</summary>
    private sealed record ResolvedBinding(Guid OwnerUnitId, UnitConnectorBinding Binding);
}
