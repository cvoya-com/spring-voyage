// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connectors;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Per-launch runtime-context contribution seam (#2380). A connector that
/// implements this interface contributes environment variables and / or
/// mounted context files into the container launched for an agent or unit
/// turn. The dispatcher resolves every binding that applies to a subject
/// (direct on the unit, or inherited from a parent unit), invokes each
/// contributor, and merges the result into <c>AgentLaunchSpec</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>When the seam runs.</b> Contributors are invoked once per container
/// launch from inside the launch flow (<c>A2AExecutionDispatcher</c>).
/// They are not invoked from any API / portal / read path — credentials
/// minted here never round-trip through user-facing responses.
/// </para>
/// <para>
/// <b>Lifecycle.</b> Per-launch. A short-lived token (or other credential
/// material) returned by a contributor lives only for the duration of the
/// container; rotation is handled implicitly by re-launching. Contributors
/// MUST NOT cache credentials across launches.
/// </para>
/// <para>
/// <b>Env-var namespace convention.</b> Every env-var contributed by this
/// seam must use the reserved namespace
/// <c>SPRING_CONNECTOR_&lt;SLUG_UPPER&gt;_*</c>, where <c>&lt;SLUG_UPPER&gt;</c>
/// is the connector's <see cref="IConnectorType.Slug"/> upper-cased with
/// non-alphanumeric characters replaced by underscores. This guarantees
/// the contributor cannot shadow platform-bootstrap names (e.g.
/// <c>SPRING_TENANT_ID</c>, <c>SPRING_MCP_TOKEN</c>) and that two
/// connectors cannot accidentally clobber each other.
/// </para>
/// <para>
/// <b>Mounted-file path convention.</b> Files are placed under
/// <c>/spring/context/connectors/&lt;slug&gt;/</c>; the dispatcher mounts
/// the merged context directory at <c>/spring/context/</c> per the D1
/// runtime spec. The contributor returns sub-paths relative to that mount
/// (e.g. <c>connectors/github/binding.json</c>); the dispatcher fills in
/// the on-host materialisation path.
/// </para>
/// <para>
/// <b>Collision rules.</b> The dispatcher enforces fail-fast on any name
/// collision — across two contributors, or between a contributor and the
/// platform bootstrap. A connector colliding with platform bootstrap is a
/// wiring bug, not a runtime concern; the dispatcher refuses to launch
/// rather than choose a winner silently.
/// </para>
/// <para>
/// <b>Authoring contract.</b> Implementers are typically registered as
/// singletons. The contributor is invoked with the resolved binding
/// payload — it does not have to re-read the binding store. Mint short-
/// lived credentials inline (e.g. via the connector's existing auth path).
/// </para>
/// </remarks>
public interface IConnectorRuntimeContextContributor
{
    /// <summary>
    /// The connector type id this contributor handles. Matches
    /// <see cref="IConnectorType.TypeId"/>. The dispatcher uses this to
    /// route a resolved binding to the matching contributor; multiple
    /// contributors registered for the same id are not supported (the
    /// dispatcher picks the first registered one and logs a warning).
    /// </summary>
    Guid ConnectorTypeId { get; }

    /// <summary>
    /// Builds the connector's contribution for one container launch.
    /// </summary>
    /// <param name="request">
    /// The per-launch context: subject address (the agent or unit being
    /// launched), the owning unit of the binding (direct or inherited),
    /// the binding payload, and the current tenant id.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The set of env vars and files this connector contributes. The
    /// contributor returns <see cref="ConnectorRuntimeContextContribution.Empty"/>
    /// when the binding does not warrant a contribution (e.g. the payload
    /// is malformed) — this is preferable to throwing because a
    /// per-binding misconfiguration should not block other connectors'
    /// contributions to the same launch.
    /// </returns>
    Task<ConnectorRuntimeContextContribution> ContributeAsync(
        ConnectorRuntimeContextRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Input to an <see cref="IConnectorRuntimeContextContributor"/>.
/// </summary>
/// <param name="Subject">
/// The address of the agent or unit being launched. Contributors that
/// want to scope a token to the subject can use this (e.g. as part of a
/// short-lived token's audience claim).
/// </param>
/// <param name="BindingOwnerUnitId">
/// The unit that owns the resolved binding. When the binding is direct
/// on the launched subject (<see cref="Subject"/> resolves to the same
/// unit) this equals the subject's id. When the binding is inherited
/// from an ancestor unit, this carries the ancestor's id so the
/// contributor can mint credentials scoped to the ancestor's
/// configuration.
/// </param>
/// <param name="Binding">
/// The persisted connector binding (TypeId + opaque JSON config). The
/// dispatcher has already filtered by <see cref="IConnectorRuntimeContextContributor.ConnectorTypeId"/>
/// so the contributor can deserialise the config directly without
/// re-checking the type.
/// </param>
/// <param name="TenantId">
/// The current tenant. Contributors that mint per-tenant credentials use
/// this; OSS-only contributors typically ignore it.
/// </param>
public record ConnectorRuntimeContextRequest(
    Address Subject,
    Guid BindingOwnerUnitId,
    UnitConnectorBinding Binding,
    Guid TenantId);

/// <summary>
/// Output of an <see cref="IConnectorRuntimeContextContributor"/>.
/// </summary>
/// <param name="EnvironmentVariables">
/// Env-var key / value pairs to stamp into the container. Keys MUST use
/// the namespace convention <c>SPRING_CONNECTOR_&lt;SLUG_UPPER&gt;_*</c>;
/// the dispatcher fails the launch if any key violates the convention or
/// collides with platform bootstrap names. Empty when the contributor has
/// nothing to add.
/// </param>
/// <param name="ContextFiles">
/// Files to materialise inside the container under
/// <c>/spring/context/</c>. Keys are sub-paths relative to that mount
/// point — e.g. <c>connectors/github/binding.json</c>. The dispatcher
/// fails the launch if two contributors write the same sub-path. Empty
/// when the contributor has nothing to add.
/// </param>
/// <param name="WellKnownAliasEnvironmentVariables">
/// Optional extra env-vars the contributor intentionally publishes
/// outside the <c>SPRING_CONNECTOR_&lt;SLUG_UPPER&gt;_*</c> namespace —
/// canonical names downstream CLI tooling already reads (#2442). The
/// canonical example is <c>GITHUB_TOKEN</c>, which the <c>gh</c> CLI
/// and <c>git</c> credential helpers consume natively; without setting
/// it, agents would have to manually re-export the namespaced value
/// before every <c>gh</c> call. Aliases are subject to the same no-
/// collision rule across contributors as <see cref="EnvironmentVariables"/>,
/// and the resolver still rejects any alias that collides with a
/// platform-bootstrap name. The namespaced var (e.g.
/// <c>SPRING_CONNECTOR_GITHUB_TOKEN</c>) remains the canonical Spring
/// Voyage interface; the alias is the convenience hop for ecosystem
/// tooling. Empty when the contributor publishes no aliases.
/// </param>
public record ConnectorRuntimeContextContribution(
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    IReadOnlyDictionary<string, string> ContextFiles,
    IReadOnlyDictionary<string, string>? WellKnownAliasEnvironmentVariables = null)
{
    /// <summary>
    /// Singleton "no contribution" instance. Contributors return this
    /// when the resolved binding does not warrant any env-vars or files
    /// (e.g. malformed config the contributor logged a warning for).
    /// </summary>
    public static readonly ConnectorRuntimeContextContribution Empty =
        new(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));
}
