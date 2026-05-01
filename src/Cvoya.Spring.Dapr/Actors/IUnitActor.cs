// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Auth;

/// <summary>
/// Dapr actor interface for unit actors. A unit is an agent — it shares the
/// mailbox / message-dispatch contract defined by <see cref="IAgent"/> —
/// with additional structure: members, human permissions, lifecycle status,
/// and a connector binding. Domain messages are delegated to the unit's
/// configured <see cref="Core.Orchestration.IOrchestrationStrategy"/>, which
/// the platform treats as one flavour of agent cognition; control messages
/// (cancel, status, health, policy) are handled directly and follow the same
/// shape as on <see cref="IAgentActor"/>.
/// </summary>
public interface IUnitActor : IAgent
{
    /// <summary>
    /// Adds a member (agent or sub-unit) to this unit.
    /// </summary>
    /// <param name="member">The address of the member to add.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task AddMemberAsync(Address member, CancellationToken ct = default);

    /// <summary>
    /// Removes a member from this unit.
    /// </summary>
    /// <param name="member">The address of the member to remove.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task RemoveMemberAsync(Address member, CancellationToken ct = default);

    /// <summary>
    /// Returns the current array of member addresses in this unit.
    /// </summary>
    /// <remarks>
    /// Bug #319: returning a concrete array avoids <c>DataContractSerializer</c>
    /// "type not expected" failures at the Dapr remoting boundary. Runtime
    /// collection types such as <see cref="System.Collections.ObjectModel.ReadOnlyCollection{T}"/>
    /// are not data-contract known types by default, so the public contract
    /// must be a type that serializes natively.
    /// </remarks>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>An array of member addresses.</returns>
    Task<Address[]> GetMembersAsync(CancellationToken ct = default);

    /// <summary>
    /// Sets the permission level for a human within this unit.
    /// </summary>
    /// <param name="humanId">The stable UUID of the human (#1491).</param>
    /// <param name="entry">The permission entry to set.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task SetHumanPermissionAsync(Guid humanId, UnitPermissionEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Removes any human permission entry for <paramref name="humanId"/> from
    /// this unit. Idempotent — removing an entry that does not exist is a
    /// no-op and completes successfully so <c>spring unit humans remove</c>
    /// is safe to retry.
    /// </summary>
    /// <param name="humanId">The stable UUID of the human (#1491).</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// <c>true</c> when an entry was removed, <c>false</c> when no entry
    /// existed for the supplied id. The API endpoint discards the bool and
    /// always returns 204 regardless of prior presence.
    /// </returns>
    Task<bool> RemoveHumanPermissionAsync(Guid humanId, CancellationToken ct = default);

    /// <summary>
    /// Gets the permission level for a human within this unit.
    /// </summary>
    /// <param name="humanId">The stable UUID of the human (#1491).</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The permission level, or <c>null</c> if the human has no permission entry.</returns>
    Task<PermissionLevel?> GetHumanPermissionAsync(Guid humanId, CancellationToken ct = default);

    /// <summary>
    /// Gets all human permission entries for this unit.
    /// </summary>
    /// <remarks>
    /// Bug #319: returns a concrete array so <c>DataContractSerializer</c> can
    /// marshal it without a <see cref="KnownTypeAttribute"/> declaration.
    /// </remarks>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>An array of all human permission entries.</returns>
    Task<UnitPermissionEntry[]> GetHumanPermissionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the persisted lifecycle status of this unit. A unit that has never transitioned reports <see cref="UnitStatus.Draft"/>.
    /// </summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The current lifecycle status.</returns>
    Task<UnitStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Attempts a lifecycle transition to <paramref name="target"/>. If the transition is not
    /// permitted from the current status, the status is left unchanged and a rejection reason is returned.
    /// </summary>
    /// <param name="target">The target status.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A <see cref="TransitionResult"/> describing success or rejection.</returns>
    Task<TransitionResult> TransitionAsync(UnitStatus target, CancellationToken ct = default);

    /// <summary>
    /// Terminal callback the <c>UnitValidationWorkflow</c> invokes when its
    /// probe run finishes. Drives the <see cref="UnitStatus.Validating"/>
    /// → <see cref="UnitStatus.Stopped"/> or
    /// <see cref="UnitStatus.Validating"/> → <see cref="UnitStatus.Error"/>
    /// transition and persists the redacted failure payload on failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two guards protect against re-entry:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///   <b>Stale-run guard.</b> If
    ///   <see cref="UnitValidationCompletion.WorkflowInstanceId"/> does not
    ///   match the unit's current <c>LastValidationRunId</c>, the callback is
    ///   a no-op: an older workflow arriving after a newer revalidation has
    ///   already been scheduled must not rewrite the current state.
    ///   </item>
    ///   <item>
    ///   <b>Terminal-status guard.</b> If the unit's current status is
    ///   already <see cref="UnitStatus.Stopped"/> or
    ///   <see cref="UnitStatus.Error"/> (e.g. a second workflow superseded
    ///   this one), the callback is also a no-op.
    ///   </item>
    /// </list>
    /// <para>
    /// On success, <c>LastValidationErrorJson</c> is cleared and the unit
    /// transitions to <see cref="UnitStatus.Stopped"/>. On failure, the
    /// failure payload is serialized (System.Text.Json) into
    /// <c>LastValidationErrorJson</c> and the unit transitions to
    /// <see cref="UnitStatus.Error"/>. Both paths emit a
    /// <c>StateChanged</c> activity event through the existing transition
    /// write path.
    /// </para>
    /// </remarks>
    /// <param name="completion">The workflow's terminal outcome.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// A <see cref="TransitionResult"/> describing the applied transition
    /// (on a guard no-op, the result reports the unchanged current status
    /// and a diagnostic rejection reason).
    /// </returns>
    Task<TransitionResult> CompleteValidationAsync(UnitValidationCompletion completion, CancellationToken ct = default);

    /// <summary>
    /// Returns the actor-owned portion of the unit's metadata. Only
    /// <c>Model</c> and <c>Color</c> are persisted on the actor; DisplayName
    /// and Description live on the directory entity and are always returned
    /// as <c>null</c> here. The API endpoint merges both sources when
    /// projecting a response.
    /// </summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The persisted metadata. Unset fields are <c>null</c>.</returns>
    Task<UnitMetadata> GetMetadataAsync(CancellationToken ct = default);

    /// <summary>
    /// Updates the actor-owned portion of the unit's metadata. Only
    /// non-<c>null</c> fields are written — a <c>null</c> field leaves the
    /// corresponding state key untouched, which makes this safe for partial
    /// PATCH-style updates. <c>DisplayName</c> and <c>Description</c> on
    /// the incoming <paramref name="metadata"/> are not persisted on the
    /// actor (they live on the directory entity) — callers that need to
    /// update those fields must do so through the directory service.
    /// Emits a <c>StateChanged</c> activity event describing which fields
    /// were updated whenever at least one field (actor-owned or
    /// directory-owned) is non-<c>null</c>, so the audit trail is consistent
    /// regardless of which fields changed.
    /// </summary>
    /// <param name="metadata">The metadata to apply.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task SetMetadataAsync(UnitMetadata metadata, CancellationToken ct = default);

    /// <summary>
    /// Gets the unit's active connector binding, or <c>null</c> when the
    /// unit is not wired to any connector. The binding identifies the
    /// connector type and carries the connector-specific typed config as an
    /// opaque <see cref="JsonElement"/> — connector-specific shape is
    /// deserialized by the owning connector package.
    /// </summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The persisted binding, or <c>null</c> if unset.</returns>
    Task<UnitConnectorBinding?> GetConnectorBindingAsync(CancellationToken ct = default);

    /// <summary>
    /// Upserts the unit's connector binding atomically. Replaces any prior
    /// binding regardless of connector type. Pass <c>null</c> to clear the
    /// binding.
    /// </summary>
    /// <param name="binding">The new binding, or <c>null</c> to clear.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task SetConnectorBindingAsync(UnitConnectorBinding? binding, CancellationToken ct = default);

    /// <summary>
    /// Returns the opaque connector-owned runtime metadata stored on the
    /// unit (e.g. a webhook id that a connector registered on /start and
    /// will need on /stop), or <c>null</c> when no metadata is stored.
    /// </summary>
    /// <param name="ct">A token to cancel the operation.</param>
    Task<JsonElement?> GetConnectorMetadataAsync(CancellationToken ct = default);

    /// <summary>
    /// Stores connector-owned runtime metadata on the unit. Pass
    /// <c>default(JsonElement)</c> or an explicit null-valued element to
    /// clear.
    /// </summary>
    /// <param name="metadata">The metadata to persist, or <c>null</c> to clear.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task SetConnectorMetadataAsync(JsonElement? metadata, CancellationToken ct = default);

    /// <summary>
    /// Evaluates whether the unit has enough configuration to leave the
    /// <see cref="UnitStatus.Draft"/> state. The result lists each
    /// unsatisfied requirement so the UI can surface actionable guidance.
    /// </summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A <see cref="ReadinessResult"/> describing readiness.</returns>
    Task<ReadinessResult> CheckReadinessAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the unit's own (non-aggregated) expertise — the domains
    /// declared on the unit itself, independent of its members. Used by
    /// <see cref="Core.Capabilities.IExpertiseAggregator"/> as one input of
    /// the recursive composition. Unit-level expertise is typically empty
    /// for leaf organizational units, but the slot exists so a unit can
    /// advertise a synthesised capability that isn't owned by any single
    /// member (see #412 / #413).
    /// </summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>
    /// Every configured <see cref="ExpertiseDomain"/> for this unit. Returns
    /// an empty array when nothing is configured.
    /// </returns>
    Task<ExpertiseDomain[]> GetOwnExpertiseAsync(CancellationToken ct = default);

    /// <summary>
    /// Replaces the unit's own (non-aggregated) expertise with
    /// <paramref name="domains"/>. Passing an empty array clears the
    /// configuration. Emits a <c>StateChanged</c> activity event on every
    /// write so the activity-stream projection in #44 can surface
    /// directory-change events without additional wiring.
    /// </summary>
    /// <param name="domains">The replacement expertise set.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task SetOwnExpertiseAsync(ExpertiseDomain[] domains, CancellationToken ct = default);

    /// <summary>
    /// Returns the unit's boundary configuration (#413) — the opacity /
    /// projection / synthesis rules that control what outside callers see
    /// when they read the unit's aggregated expertise. A unit that has
    /// never had a boundary persisted returns
    /// <see cref="Core.Capabilities.UnitBoundary.Empty"/> — callers never
    /// have to branch on "has a boundary".
    /// </summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The persisted boundary, or an empty boundary when none.</returns>
    Task<UnitBoundary> GetBoundaryAsync(CancellationToken ct = default);

    /// <summary>
    /// Upserts the unit's boundary configuration (#413). Passing an empty
    /// boundary (no rules in any slot) is a valid "clear all rules"
    /// operation — the actor represents that as a row deletion. Emits a
    /// <c>StateChanged</c> activity event on every write so the activity
    /// stream reflects boundary changes.
    /// </summary>
    /// <param name="boundary">The boundary configuration to persist.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task SetBoundaryAsync(UnitBoundary boundary, CancellationToken ct = default);

    /// <summary>
    /// Returns the unit's permission-inheritance mode (#414). A unit that
    /// has never had the setting explicitly written defaults to
    /// <see cref="UnitPermissionInheritance.Inherit"/> — ancestor grants
    /// flow down through this unit. Flipping to
    /// <see cref="UnitPermissionInheritance.Isolated"/> makes this unit the
    /// permission-layer analogue of an opaque boundary: ancestor grants are
    /// ignored for humans whose only rights come from up the chain.
    /// </summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The persisted inheritance mode, or the default when unset.</returns>
    Task<UnitPermissionInheritance> GetPermissionInheritanceAsync(CancellationToken ct = default);

    /// <summary>
    /// Upserts the unit's permission-inheritance mode (#414). Writes
    /// <see cref="UnitPermissionInheritance.Inherit"/> as a row deletion so
    /// clearing the flag returns to the default without keeping a no-op
    /// state row around. Emits a <c>StateChanged</c> activity event on
    /// every write so the activity stream reflects permission-model changes.
    /// </summary>
    /// <param name="inheritance">The inheritance mode to persist.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task SetPermissionInheritanceAsync(UnitPermissionInheritance inheritance, CancellationToken ct = default);
}