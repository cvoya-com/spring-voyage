// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Secrets;

/// <summary>
/// Records which registry entry produced a resolved plaintext value. The
/// resolver returns this alongside the value via
/// <see cref="SecretResolution"/> so the audit-log decorator (tracked by
/// a separate wave) can record not just "what was read" but "via what
/// path" — an explicit signal that inheritance/fall-through fired.
///
/// <para>
/// New values may be appended in later waves (e.g. platform fall-through,
/// per-agent ACL matches). Treat the enum as open for extension.
/// </para>
/// </summary>
public enum SecretResolvePath
{
    /// <summary>
    /// The requested (scope, owner, name) triple did not resolve — no
    /// direct entry and no inheritance match.
    /// </summary>
    NotFound = 0,

    /// <summary>
    /// The requested (scope, owner, name) triple resolved directly — no
    /// inheritance was applied.
    /// </summary>
    Direct = 1,

    /// <summary>
    /// The requested triple was <see cref="SecretScope.Unit"/>, the unit
    /// entry was missing, and the resolver fell through to the
    /// tenant-scoped entry with the same name. Both unit-level and
    /// tenant-level access-policy checks passed.
    /// </summary>
    InheritedFromTenant = 2,
}