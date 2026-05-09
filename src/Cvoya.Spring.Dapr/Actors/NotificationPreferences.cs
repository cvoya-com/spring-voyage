// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

/// <summary>
/// Persisted notification-routing preferences for a human user. Stored as
/// a <c>jsonb</c> column on the <c>humans</c> table per
/// <a href="../../../docs/decisions/0040-actor-state-ownership-matrix.md">ADR-0040</a>.
/// </summary>
/// <param name="EmailEnabled">
/// When <c>true</c>, the human consents to e-mail notifications.
/// </param>
/// <param name="InAppEnabled">
/// When <c>true</c>, the human consents to in-app (portal / inbox)
/// notifications. Inbox delivery is the v0.1 default.
/// </param>
/// <remarks>
/// Notification routing itself is future work; this record exists so the
/// platform can persist the preference shape today and the routing layer
/// can read it when it ships. The shape is intentionally minimal — adding
/// fields is additive on a <c>jsonb</c> column.
/// </remarks>
public sealed record NotificationPreferences(
    bool EmailEnabled = false,
    bool InAppEnabled = true);
