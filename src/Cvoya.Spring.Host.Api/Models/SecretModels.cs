// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using Cvoya.Spring.Core.Secrets;

/// <summary>
/// Metadata entry returned by secret-listing endpoints. Never contains
/// the plaintext value or the underlying store key — plaintext only
/// enters the system via <see cref="CreateSecretRequest"/> and leaves
/// exclusively through server-side <see cref="ISecretResolver.ResolveAsync"/>.
/// </summary>
/// <param name="Name">The secret name.</param>
/// <param name="Scope">The secret scope.</param>
/// <param name="CreatedAt">Registration timestamp.</param>
public record SecretMetadata(
    string Name,
    SecretScope Scope,
    DateTimeOffset CreatedAt);

/// <summary>
/// Response body for GET <c>/api/v1/units/{id}/secrets</c>.
/// </summary>
/// <param name="Secrets">The metadata entries for this unit.</param>
public record UnitSecretsListResponse(IReadOnlyList<SecretMetadata> Secrets);

/// <summary>
/// Response body for scope-keyed secret listing endpoints
/// (<c>GET /api/v1/tenant/secrets</c>, <c>GET /api/v1/platform/secrets</c>).
/// Mirrors <see cref="UnitSecretsListResponse"/> — same contract, the two
/// shapes only differ in name to keep the unit-scoped response stable.
/// </summary>
/// <param name="Secrets">The metadata entries for the scope/owner.</param>
public record SecretsListResponse(IReadOnlyList<SecretMetadata> Secrets);

/// <summary>
/// Request body for POST <c>/api/v1/units/{id}/secrets</c>. Exactly one
/// of <paramref name="Value"/> or <paramref name="ExternalStoreKey"/>
/// must be provided — "pass-through" write vs "external reference"
/// binding. Supplying both or neither yields 400 Bad Request.
/// </summary>
/// <param name="Name">The secret name (case-sensitive).</param>
/// <param name="Value">
/// Plaintext to store via <see cref="ISecretStore.WriteAsync"/>. Leaves
/// the HTTP layer exactly once (on this request) and is never echoed in
/// any response body or log.
/// </param>
/// <param name="ExternalStoreKey">
/// An externally-managed store key (e.g. a Key Vault secret id) to bind
/// this secret name to. No plaintext is written by the server when this
/// mode is used.
/// </param>
public record CreateSecretRequest(
    string Name,
    string? Value = null,
    string? ExternalStoreKey = null);

/// <summary>
/// Response body returned by the pass-through and external-reference
/// write endpoints. Mirrors <see cref="SecretMetadata"/>; the value /
/// store key are never returned.
/// </summary>
/// <param name="Name">The secret name.</param>
/// <param name="Scope">The secret scope.</param>
/// <param name="CreatedAt">Registration timestamp.</param>
public record CreateSecretResponse(
    string Name,
    SecretScope Scope,
    DateTimeOffset CreatedAt);

/// <summary>
/// Request body for PUT <c>/api/v1/units/{id}/secrets/{name}</c>
/// (and the tenant / platform mirrors). Carries the replacement value or
/// external pointer; the registry appends a new version atomically.
/// Exactly one of <paramref name="Value"/> or
/// <paramref name="ExternalStoreKey"/> must be provided — the same
/// discriminated shape as <see cref="CreateSecretRequest"/>.
///
/// <para>
/// The secret <c>Name</c> is taken from the route — rotation never
/// renames an entry, so the body intentionally omits the name field
/// (unlike <see cref="CreateSecretRequest"/>).
/// </para>
/// </summary>
/// <param name="Value">Replacement plaintext for pass-through rotation. Mutually exclusive with <paramref name="ExternalStoreKey"/>.</param>
/// <param name="ExternalStoreKey">Replacement external-reference pointer. Mutually exclusive with <paramref name="Value"/>.</param>
public record RotateSecretRequest(
    string? Value = null,
    string? ExternalStoreKey = null);

/// <summary>
/// Response body returned by the rotate endpoints (wave 7 A5). Echoes
/// the new version number assigned by the registry so callers (CI
/// pipelines, CLIs) can pin subsequent reads to the new version.
/// </summary>
/// <param name="Name">The secret name.</param>
/// <param name="Scope">The secret scope.</param>
/// <param name="Version">The new version number assigned by rotation.</param>
public record RotateSecretResponse(
    string Name,
    SecretScope Scope,
    int Version);

/// <summary>
/// Per-version metadata entry returned by
/// <c>GET /api/v1/.../secrets/{name}/versions</c>. Metadata-only —
/// the opaque store key is NEVER included. Consumers sort client-side
/// if they need a particular order.
/// </summary>
/// <param name="Version">The version number (monotonically increasing per name).</param>
/// <param name="Origin">Who owns the backing store slot for this version.</param>
/// <param name="CreatedAt">Registration timestamp for this version.</param>
/// <param name="IsCurrent"><c>true</c> when this is the latest (most-recent) version.</param>
public record SecretVersionEntry(
    int Version,
    SecretOrigin Origin,
    DateTimeOffset CreatedAt,
    bool IsCurrent);

/// <summary>
/// Response body for <c>GET /api/v1/.../secrets/{name}/versions</c>.
/// </summary>
/// <param name="Name">The secret name.</param>
/// <param name="Scope">The secret scope.</param>
/// <param name="Versions">The retained versions for this secret, newest first.</param>
public record SecretVersionsListResponse(
    string Name,
    SecretScope Scope,
    IReadOnlyList<SecretVersionEntry> Versions);

/// <summary>
/// Response body for <c>POST /api/v1/.../secrets/{name}/prune</c>.
/// </summary>
/// <param name="Name">The secret name.</param>
/// <param name="Scope">The secret scope.</param>
/// <param name="Keep">The number of most-recent versions retained.</param>
/// <param name="Pruned">The number of version rows removed from the registry.</param>
public record PruneSecretResponse(
    string Name,
    SecretScope Scope,
    int Keep,
    int Pruned);