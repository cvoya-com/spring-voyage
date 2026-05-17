// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connectors;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Per-launch prompt-context contribution seam (#2442). A connector that
/// implements this interface contributes a markdown fragment to the
/// platform layer of the four-layer prompt assembly — telling the agent
/// what env-vars the binding sets, what bound resource identity it
/// represents, and how to use the connector's CLI tools.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate seam from <see cref="IConnectorRuntimeContextContributor"/>?</b>
/// The two contributions have different consumers (container env-vars
/// vs prompt text) and may evolve independently. A connector type
/// implements either or both. The dispatcher resolves both seams from
/// the same binding walk (direct + inherited bindings on the launch
/// subject) but feeds the results into different parts of the launch
/// spec.
/// </para>
/// <para>
/// <b>When the seam runs.</b> Contributors are invoked once per
/// container launch from inside the launch flow
/// (<c>A2AExecutionDispatcher</c>). They are not invoked from any API
/// / portal / read path — the fragments are not user-facing output and
/// never round-trip through API responses.
/// </para>
/// <para>
/// <b>Lifecycle.</b> Per-launch. Fragments are recomputed on every
/// launch so the rendered prompt always reflects the current binding
/// state (e.g. the freshly-resolved owner / repo).
/// </para>
/// <para>
/// <b>Fragment shape.</b> The fragment is a markdown block. The
/// platform layer wraps the concatenated fragments in a
/// <c>## Connector context (auto-injected by platform)</c> heading.
/// Each contributor is expected to start with a <c>### …</c> sub-
/// heading that identifies the binding (e.g.
/// <c>### GitHub binding — cvoya-com/spring-voyage</c>) so multiple
/// connectors render cleanly side-by-side.
/// </para>
/// <para>
/// <b>Authoring contract.</b> Implementers are typically registered as
/// singletons via <c>TryAddEnumerable</c> so cloud overlays can pre-
/// register a tenant-aware variant. The fragment generation uses the
/// resolved binding payload — it does not have to re-read the binding
/// store. Returning <c>null</c> is the canonical "no hints for this
/// subject" signal; the resolver simply skips it (no fall-back to any
/// legacy text path).
/// </para>
/// </remarks>
public interface IConnectorPromptContextContributor
{
    /// <summary>
    /// The connector type id this contributor handles. Matches
    /// <see cref="IConnectorType.TypeId"/>. The resolver uses this to
    /// route a resolved binding to the matching contributor; multiple
    /// contributors registered for the same id are not supported (the
    /// resolver picks the first registered one and logs a warning,
    /// mirroring the runtime-context resolver's behaviour).
    /// </summary>
    Guid ConnectorTypeId { get; }

    /// <summary>
    /// Builds the connector's prompt-context contribution for one
    /// container launch.
    /// </summary>
    /// <param name="subject">
    /// The address of the agent or unit being launched. Contributors
    /// that want to specialise the fragment per-subject can use this;
    /// most do not.
    /// </param>
    /// <param name="bindingOwnerUnitId">
    /// The unit that owns the resolved binding. When the binding is
    /// direct on the launched subject this equals the subject's id.
    /// When the binding is inherited from an ancestor unit, this
    /// carries the ancestor's id so the contributor can refer to the
    /// declaring scope if it wants to.
    /// </param>
    /// <param name="binding">
    /// The persisted connector binding (TypeId + opaque JSON config).
    /// The resolver has already filtered by
    /// <see cref="ConnectorTypeId"/> so the contributor can
    /// deserialise the config directly.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The markdown fragment to inject into the platform layer for
    /// this binding, or <c>null</c> when the contributor has nothing
    /// to say about this subject (e.g. the binding payload is
    /// malformed — the contributor logged a warning and chose to skip
    /// silently). <c>null</c> is preferable to throwing because a
    /// per-binding misconfiguration should not block other connectors'
    /// fragments on the same launch.
    /// </returns>
    Task<string?> GetPromptHintsAsync(
        Address subject,
        Guid bindingOwnerUnitId,
        UnitConnectorBinding binding,
        CancellationToken cancellationToken = default);
}
