// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Security;

/// <summary>
/// Two-way resolver between a stable human UUID and a connector-native
/// user identifier (e.g. a GitHub login). Implementations consult the
/// <c>human_connector_identities</c> table (#2408) per the v0.1
/// dogfooding surfacing model.
/// </summary>
/// <remarks>
/// <para>
/// The platform's surfacing model keeps two complementary identity spaces
/// active at once: <c>sv.*</c> MCP tools operate on UUIDs (stable, never
/// renamed) while container-native CLI tools (<c>gh</c>, <c>git</c>) operate
/// on connector-native ids supplied via the runtime-context env-vars from
/// #2380. This resolver is the bridge — given one form, return the other.
/// </para>
/// <para>
/// Implementations MUST honour the tenant query filter: a lookup that
/// would cross the tenant boundary returns <c>null</c>. The OSS default
/// implementation in <c>Cvoya.Spring.Dapr</c> is registered
/// <c>TryAddScoped</c> so the hosted overlay can substitute a tenant-aware
/// or decorating implementation.
/// </para>
/// </remarks>
public interface IHumanConnectorIdentityResolver
{
    /// <summary>
    /// Returns the human a connector identity points at, or <c>null</c>
    /// when no row maps the supplied login to a human in the current
    /// tenant. Used by inbound paths that arrive with a connector-native
    /// id (e.g. a webhook payload's reviewer login) and need to address
    /// the human in platform-native form.
    /// </summary>
    /// <param name="connectorId">
    /// The connector slug (matches <c>IConnectorType.Slug</c>, e.g. <c>github</c>).
    /// </param>
    /// <param name="connectorUserId">
    /// The connector-native user identifier — for GitHub this is the
    /// login string without the leading <c>@</c>.
    /// </param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<HumanConnectorIdentity?> ResolveHumanAsync(
        string connectorId,
        string connectorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the connector-native user id a human is mapped to on the
    /// supplied connector, or <c>null</c> when no row exists. Used by
    /// outbound paths that hold a human UUID (because a workflow / sv.*
    /// tool resolved it) and need the connector-native id to call the
    /// remote API.
    /// </summary>
    /// <param name="humanId">The stable human UUID.</param>
    /// <param name="connectorId">The connector slug.</param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<string?> ResolveUserIdAsync(
        Guid humanId,
        string connectorId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// DTO returned by <see cref="IHumanConnectorIdentityResolver.ResolveHumanAsync"/>.
/// Carries the resolved human's identity alongside the matched mapping
/// row's display fields so a caller can render the result without a
/// second DB round-trip.
/// </summary>
/// <param name="HumanId">The stable human UUID the identity maps to.</param>
/// <param name="ConnectorId">The connector slug (e.g. <c>github</c>).</param>
/// <param name="ConnectorUserId">The connector-native user id (e.g. a GitHub login).</param>
/// <param name="DisplayHandle">
/// Optional human-readable label stored alongside the row; <c>null</c>
/// when the operator did not supply one.
/// </param>
public sealed record HumanConnectorIdentity(
    Guid HumanId,
    string ConnectorId,
    string ConnectorUserId,
    string? DisplayHandle);
