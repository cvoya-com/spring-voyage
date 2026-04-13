// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Secrets;

/// <summary>
/// The detailed result of an <see cref="ISecretResolver.ResolveWithPathAsync"/>
/// call. Exposes the plaintext value along with the
/// <see cref="SecretResolvePath"/> that produced it and the effective
/// <see cref="SecretRef"/> whose registry entry was read. The effective
/// reference differs from the requested reference only when inheritance
/// fired — e.g. a request for
/// <c>(Unit, "engineering", "gh-token")</c> that falls through to the
/// tenant will return an effective reference of
/// <c>(Tenant, tenantId, "gh-token")</c>.
///
/// <para>
/// Decorators wrapping <see cref="ISecretResolver"/> (audit-log, RBAC,
/// rotation, metrics) use this shape to emit structured records without
/// needing any private state on the resolver.
/// </para>
/// </summary>
/// <param name="Value">
/// The resolved plaintext value, or <c>null</c> when
/// <see cref="Path"/> is <see cref="SecretResolvePath.NotFound"/>.
/// </param>
/// <param name="Path">
/// Which registry entry produced the value (or indicated "not found").
/// </param>
/// <param name="EffectiveRef">
/// The structural reference actually read when <see cref="Path"/> is
/// <see cref="SecretResolvePath.Direct"/> or
/// <see cref="SecretResolvePath.InheritedFromTenant"/>;
/// <c>null</c> for <see cref="SecretResolvePath.NotFound"/>.
/// </param>
public record SecretResolution(
    string? Value,
    SecretResolvePath Path,
    SecretRef? EffectiveRef);