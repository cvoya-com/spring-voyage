// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Request body for upserting a human ↔ connector identity mapping
/// (#2408). Mirrors the columns on <c>human_connector_identities</c>;
/// the URL carries the human id while the body carries the connector
/// half of the tuple.
/// </summary>
/// <param name="ConnectorId">
/// The connector slug (e.g. <c>github</c>). Must match an installed
/// connector's <c>IConnectorType.Slug</c>; the server does not enforce
/// the slug-must-be-installed invariant so identities can be staged
/// before a connector lands.
/// </param>
/// <param name="ConnectorUserId">
/// The connector-native user identifier — for GitHub this is the
/// login string without the leading <c>@</c>.
/// </param>
/// <param name="DisplayHandle">
/// Optional human-readable label rendered in <c>spring human identity list</c>.
/// </param>
public sealed record HumanConnectorIdentityRequest(
    [property: Required] string ConnectorId,
    [property: Required] string ConnectorUserId,
    string? DisplayHandle);

/// <summary>
/// Response body for the human ↔ connector identity routes (#2408).
/// Carries the resolved tuple plus audit timestamps so the CLI / portal
/// can render the row without a follow-up GET.
/// </summary>
/// <param name="HumanId">The stable human UUID this identity maps to.</param>
/// <param name="ConnectorId">The connector slug.</param>
/// <param name="ConnectorUserId">The connector-native user id.</param>
/// <param name="DisplayHandle">The optional display label.</param>
/// <param name="CreatedAt">UTC timestamp when the mapping was first inserted.</param>
/// <param name="UpdatedAt">UTC timestamp of the most recent update.</param>
public sealed record HumanConnectorIdentityResponse(
    Guid HumanId,
    string ConnectorId,
    string ConnectorUserId,
    string? DisplayHandle,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
