// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack;

using System.Text.Json.Serialization;

/// <summary>
/// Per-<c>TenantUser</c> Slack display-identity config. The CLR shape
/// of the row the platform persists in
/// <c>tenant_user_connector_identities</c> for the Slack connector
/// per ADR-0047 §4 — strictly display-identity, never an auth surface.
///
/// <para>
/// In OSS v0.1 the operator <c>TenantUser</c> is the only mapped
/// user; cloud carries one row per bound Slack user.
/// </para>
/// </summary>
/// <param name="SlackUserId">
/// The Slack <c>user_id</c> (e.g. <c>U123456</c>) — opaque to SV; used
/// for rendering and routing only, never as an authorization principal.
/// </param>
/// <param name="DisplayName">
/// Optional human-friendly handle for this user. When <c>null</c>,
/// render sites fall back to <see cref="SlackUserId"/>.
/// </param>
public sealed record TenantSlackUserConfig(
    [property: JsonPropertyName("slack_user_id")] string SlackUserId,
    [property: JsonPropertyName("display_name")] string? DisplayName);
