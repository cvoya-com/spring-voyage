// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Secrets;

/// <summary>
/// The ownership scope for a secret. Every secret is identified by the
/// triple (<see cref="SecretScope"/>, OwnerId, Name), so two different
/// units can both have a secret called "github-token" without collision.
/// </summary>
public enum SecretScope
{
    /// <summary>Scoped to a single unit. OwnerId is the unit Guid.</summary>
    Unit = 0,

    /// <summary>Scoped to the whole tenant. OwnerId is the tenant Guid.</summary>
    Tenant = 1,

    /// <summary>Scoped to the platform. OwnerId is <c>null</c>.</summary>
    Platform = 2,
}