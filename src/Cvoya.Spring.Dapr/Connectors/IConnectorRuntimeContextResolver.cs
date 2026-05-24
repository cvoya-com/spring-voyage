// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Launch-path resolver that walks every connector binding applicable to a
/// dispatch subject (the agent or unit being launched), invokes each
/// connector's <see cref="IConnectorRuntimeContextContributor"/>, and merges
/// the contributions into the final launch context. Introduced in #2380 as
/// the bridge between the connector-binding store and the agent container's
/// runtime environment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Resolution covers direct bindings on the subject's unit
/// AND inherited bindings walked through <see cref="Cvoya.Spring.Core.Units.IUnitHierarchyResolver"/>.
/// For an <c>agent:</c> subject, the resolver first looks up the agent's
/// primary parent unit (first membership by <c>CreatedAt</c>) before
/// walking the unit's parent chain. For a <c>unit:</c> subject the walk
/// starts on the unit itself.
/// </para>
/// <para>
/// <b>Direct vs inherited.</b> When the same connector type id appears on
/// both the subject and an ancestor, the subject's direct binding wins —
/// inherited bindings only contribute when the subject (or a closer
/// ancestor) has no binding of that type. This mirrors how every other
/// inheritance walk in the platform treats specific-wins-over-general.
/// </para>
/// <para>
/// <b>Collision enforcement.</b> The resolver fails fast on:
/// <list type="bullet">
///   <item><description>An env-var key contributed by two different
///   connectors.</description></item>
///   <item><description>A context-file sub-path contributed by two
///   different connectors.</description></item>
///   <item><description>An env-var key that does not respect the
///   <c>SPRING_CONNECTOR_&lt;SLUG_UPPER&gt;_*</c> namespace
///   convention.</description></item>
/// </list>
/// Collisions with platform-bootstrap names are caught by the dispatcher's
/// final merge step — the resolver's contract is internal correctness
/// (every key matches the connector's slug) so any wider conflict surfaces
/// at the dispatcher boundary with a clear stack.
/// </para>
/// <para>
/// <b>Failure handling.</b> A contributor that throws aborts the launch —
/// silently dropping its contribution would leave the container with a
/// partial runtime environment (e.g. owner but no token), which is worse
/// than a clean dispatch failure. A contributor that has nothing to
/// contribute returns <see cref="ConnectorRuntimeContextContribution.Empty"/>;
/// the resolver treats Empty as a no-op and continues with the next
/// binding.
/// </para>
/// </remarks>
public interface IConnectorRuntimeContextResolver
{
    /// <summary>
    /// Builds the merged runtime-context contribution for the supplied
    /// subject. The returned value is suitable for merging directly into
    /// <see cref="Cvoya.Spring.Core.Execution.AgentLaunchSpec.EnvironmentVariables"/>
    /// and the bootstrap bundle's file list (ADR-0055). Files are returned
    /// keyed on a workspace-relative sub-path under
    /// <c>.spring/connectors/&lt;slug&gt;/</c> (the platform-controlled
    /// namespace per ADR-0058).
    /// </summary>
    /// <param name="subject">
    /// The dispatch target. Must be an <c>agent:</c> or <c>unit:</c>
    /// address — any other scheme is treated as a no-op.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the resolution.</param>
    /// <returns>
    /// The merged contribution, with namespace-validated env-var keys and
    /// sub-paths the dispatcher can hand to the launcher's context-files
    /// merge step. The merged result is <see cref="ConnectorRuntimeContextContribution.Empty"/>
    /// when no binding contributed anything.
    /// </returns>
    Task<ConnectorRuntimeContextContribution> ResolveAsync(
        Address subject,
        CancellationToken cancellationToken = default);
}
