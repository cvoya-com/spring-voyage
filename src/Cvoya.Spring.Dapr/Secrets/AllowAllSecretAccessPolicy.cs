// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Secrets;

using Cvoya.Spring.Core.Secrets;

/// <summary>
/// Default OSS <see cref="ISecretAccessPolicy"/> that authorizes every
/// request. The OSS host runs without authentication and has no notion
/// of tenant-admin / platform-admin roles, so the access-policy hook
/// simply exists as a DI extension point for the private cloud repo to
/// override with real role checks.
/// </summary>
public class AllowAllSecretAccessPolicy : ISecretAccessPolicy
{
    /// <inheritdoc />
    public Task<bool> IsAuthorizedAsync(
        SecretAccessAction action,
        SecretScope scope,
        Guid? ownerId,
        CancellationToken ct) => Task.FromResult(true);
}