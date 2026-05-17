// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Security;

/// <summary>
/// Seam connector packages call when an inbound binding-config write
/// carries a connector-native identifier that should be associated with
/// the operator's human (e.g. <c>UnitGitHubConfig.Reviewer</c>). The
/// default OSS implementation resolves the operator's stable human UUID
/// from the ambient HTTP request and writes a row to
/// <c>human_connector_identities</c> when one is not already present
/// (#2408). The hosted overlay swaps this for tenant-aware multi-human
/// resolution (#2411).
/// </summary>
/// <remarks>
/// <para>
/// Calls are idempotent. Re-running with the same
/// <c>(tenant, connectorId, connectorUserId)</c> tuple produces no
/// duplicate rows. When another human already owns the tuple in the
/// current tenant the seed call is a no-op so the binding write itself
/// keeps the existing mapping intact; surfacing the conflict as an
/// error here would break the binding-config write path.
/// </para>
/// <para>
/// Implementations must be safe to invoke from any code path that
/// reaches the connector's <c>MapRoutes</c> handler. The OSS default
/// resolves the operator from the ambient HTTP context; if no
/// authenticated principal is present, the seed call is a no-op rather
/// than throwing.
/// </para>
/// </remarks>
public interface IConnectorIdentityAutoSeed
{
    /// <summary>
    /// Upserts a row in <c>human_connector_identities</c> binding the
    /// authenticated caller's stable human UUID to the supplied
    /// connector-native identifier. Idempotent; never throws on
    /// conflicts.
    /// </summary>
    /// <param name="connectorId">
    /// The connector slug (e.g. <c>github</c>).
    /// </param>
    /// <param name="connectorUserId">
    /// The connector-native user identifier — for GitHub this is the
    /// login string without the leading <c>@</c>.
    /// </param>
    /// <param name="displayHandle">
    /// Optional display label persisted alongside the row.
    /// </param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task SeedForCallerAsync(
        string connectorId,
        string connectorUserId,
        string? displayHandle = null,
        CancellationToken cancellationToken = default);
}
