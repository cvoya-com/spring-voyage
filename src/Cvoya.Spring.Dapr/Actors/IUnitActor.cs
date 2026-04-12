// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Auth;

using global::Dapr.Actors;

/// <summary>
/// Dapr actor interface for unit actors.
/// A unit groups agents and sub-units, dispatching domain messages
/// through a configurable <see cref="Core.Orchestration.IOrchestrationStrategy"/>.
/// </summary>
public interface IUnitActor : IActor
{
    /// <summary>
    /// Receives and processes a message, optionally returning a response.
    /// Control messages are handled directly; domain messages are delegated
    /// to the configured orchestration strategy.
    /// </summary>
    /// <param name="message">The message to process.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>An optional response message, or <c>null</c> if no response is needed.</returns>
    Task<Message?> ReceiveAsync(Message message, CancellationToken ct = default);

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
    /// Returns the current list of member addresses in this unit.
    /// </summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A read-only list of member addresses.</returns>
    Task<IReadOnlyList<Address>> GetMembersAsync(CancellationToken ct = default);

    /// <summary>
    /// Sets the permission level for a human within this unit.
    /// </summary>
    /// <param name="humanId">The human's identifier.</param>
    /// <param name="entry">The permission entry to set.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task SetHumanPermissionAsync(string humanId, UnitPermissionEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Gets the permission level for a human within this unit.
    /// </summary>
    /// <param name="humanId">The human's identifier.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The permission level, or <c>null</c> if the human has no permission entry.</returns>
    Task<PermissionLevel?> GetHumanPermissionAsync(string humanId, CancellationToken ct = default);

    /// <summary>
    /// Gets all human permission entries for this unit.
    /// </summary>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A read-only list of all human permission entries.</returns>
    Task<IReadOnlyList<UnitPermissionEntry>> GetHumanPermissionsAsync(CancellationToken ct = default);

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
    /// the incoming <paramref name="metadata"/> are ignored by the actor
    /// (they live on the directory entity); callers that need to update
    /// those fields must do so through the directory service. Emits a
    /// <c>StateChanged</c> activity event describing which fields were
    /// written when at least one actor-owned field is non-<c>null</c>.
    /// </summary>
    /// <param name="metadata">The metadata to apply.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    Task SetMetadataAsync(UnitMetadata metadata, CancellationToken ct = default);
}