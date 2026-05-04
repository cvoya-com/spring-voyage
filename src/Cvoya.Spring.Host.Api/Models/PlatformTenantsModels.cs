// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Response payload for a single tenant record. Returned by
/// <c>GET /api/v1/platform/tenants/{id}</c>, the items of
/// <see cref="TenantsListResponse"/>, and the create / update success paths.
/// </summary>
/// <param name="Id">Stable tenant id (matches <see cref="TenantRecord.Id"/>).</param>
/// <param name="DisplayName">Human-facing display name; defaults to <paramref name="Id"/> on create.</param>
/// <param name="State">Lifecycle state — <see cref="TenantState.Active"/> or <see cref="TenantState.Deleted"/>.</param>
/// <param name="CreatedAt">Creation timestamp.</param>
/// <param name="UpdatedAt">Last-update timestamp.</param>
public sealed record TenantResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("state")] TenantState State,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

/// <summary>
/// Response payload for <c>GET /api/v1/platform/tenants</c>. Wraps the
/// list in a single object so the response shape can grow (paging,
/// continuation tokens) without breaking existing callers.
/// </summary>
/// <param name="Items">The tenant records, ordered by id.</param>
public sealed record TenantsListResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<TenantResponse> Items);

/// <summary>
/// Request body for <c>POST /api/v1/platform/tenants</c>.
/// </summary>
/// <param name="Id">
/// Stable tenant id. Per #1629 the type is a Guid; the wire form is the
/// canonical 32-character no-dash hex (matching
/// <see cref="Cvoya.Spring.Core.Identifiers.GuidFormatter.Format"/>).
/// Parsing is lenient and also accepts the dashed
/// <c>8-4-4-4-12</c> form so copy-pasted ids continue to work; values
/// that do not parse as a Guid are rejected with 400.
/// </param>
/// <param name="DisplayName">
/// Optional human-facing display name. Defaults to the canonical
/// no-dash form of <paramref name="Id"/> when null or whitespace.
/// </param>
public sealed record CreateTenantRequest(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("displayName")] string? DisplayName);

/// <summary>
/// Request body for <c>PATCH /api/v1/platform/tenants/{id}</c>. Every
/// field is optional — null leaves the corresponding column unchanged.
/// </summary>
/// <param name="DisplayName">
/// New display name. <c>null</c> leaves the existing value intact; an
/// empty / whitespace string falls back to the tenant id.
/// </param>
public sealed record UpdateTenantRequest(
    [property: JsonPropertyName("displayName")] string? DisplayName);