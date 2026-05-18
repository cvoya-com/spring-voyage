// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IConnectorPromptContextResolver"/> implementation
/// (#2442).
/// </summary>
/// <remarks>
/// <para>
/// Uses the shared <see cref="ConnectorBindingWalker"/> to collect the
/// subject's direct + inherited bindings, then invokes each
/// connector's <see cref="IConnectorPromptContextContributor"/> in the
/// order the walk returned them. Fragments are returned to the caller
/// as a flat ordered list; the assembler is responsible for the
/// section heading and the separators between fragments.
/// </para>
/// <para>
/// Failure model: a contributor that throws aborts the whole resolve —
/// silently dropping its fragment would leave the agent's prompt with
/// a half-rendered section and no way to notice. A contributor that
/// has nothing to say returns <c>null</c> and the resolver simply
/// skips it.
/// </para>
/// </remarks>
public class ConnectorPromptContextResolver(
    ConnectorBindingWalker bindingWalker,
    IEnumerable<IConnectorType> connectorTypes,
    IEnumerable<IConnectorPromptContextContributor> contributors,
    ILogger<ConnectorPromptContextResolver> logger) : IConnectorPromptContextResolver
{
    private readonly Dictionary<Guid, IConnectorType> _connectorTypesById =
        BuildConnectorTypeMap(connectorTypes);

    private readonly Dictionary<Guid, IConnectorPromptContextContributor> _contributorsByType =
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

    private static Dictionary<Guid, IConnectorPromptContextContributor> BuildContributorMap(
        IEnumerable<IConnectorPromptContextContributor> contributors,
        ILogger logger)
    {
        var map = new Dictionary<Guid, IConnectorPromptContextContributor>();
        foreach (var contributor in contributors)
        {
            if (map.ContainsKey(contributor.ConnectorTypeId))
            {
                logger.LogWarning(
                    "Multiple IConnectorPromptContextContributor implementations registered for type {TypeId}; " +
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
    public async Task<IReadOnlyList<string>> ResolveAsync(
        Address subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (_contributorsByType.Count == 0)
        {
            return [];
        }

        var bindings = await bindingWalker.WalkAsync(subject, cancellationToken);
        if (bindings.Count == 0)
        {
            return [];
        }

        var fragments = new List<string>(capacity: bindings.Count);
        foreach (var entry in bindings)
        {
            if (!_contributorsByType.TryGetValue(entry.Binding.TypeId, out var contributor))
            {
                // A binding for a connector that ships no prompt
                // contributor is a perfectly legal configuration. Skip
                // silently — the connector simply has no hints to add.
                continue;
            }

            var slug = _connectorTypesById.TryGetValue(entry.Binding.TypeId, out var ct)
                ? ct.Slug
                : throw new SpringException(
                    $"Connector type {entry.Binding.TypeId} declares a prompt-context contributor but no " +
                    "IConnectorType is registered under that id.");

            string? fragment;
            try
            {
                fragment = await contributor.GetPromptHintsAsync(
                    subject,
                    entry.OwnerUnitId,
                    entry.Binding,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SpringException(
                    $"Connector '{slug}' (type {entry.Binding.TypeId}) failed to contribute prompt hints " +
                    $"for subject {subject} (owner unit {entry.OwnerUnitId:N}): {ex.Message}",
                    ex);
            }

            if (string.IsNullOrWhiteSpace(fragment))
            {
                continue;
            }

            fragments.Add(fragment);

            logger.LogInformation(
                "Connector '{Slug}' contributed a prompt-context fragment ({Length} chars) for subject {Subject} " +
                "from binding on unit {OwnerUnit:N}",
                slug, fragment.Length, subject, entry.OwnerUnitId);
        }

        return fragments;
    }
}
