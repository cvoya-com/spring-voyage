// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

/// <summary>
/// Resolves <see cref="SkillBundleReference"/> coordinates to concrete
/// <see cref="SkillBundle"/> values. The default OSS implementation reads the
/// <c>packages/</c> tree on disk; the private cloud repo swaps in a tenant-
/// scoped implementation that reads from blob storage. Call sites only depend
/// on this interface so no downstream code has to change when the backing
/// store moves.
/// </summary>
/// <remarks>
/// <para>
/// Throws <see cref="SkillBundlePackageNotFoundException"/> when the package
/// directory cannot be located. Throws <see cref="SkillBundleNotFoundException"/>
/// when the package exists but the named skill does not. Both exceptions
/// carry the requested identifiers plus a diagnostic hint (search path,
/// available skill names) so operators can fix the manifest quickly.
/// </para>
/// <para>
/// A bundle without a companion <c>*.tools.json</c> file is still a valid
/// bundle — its <see cref="SkillBundle.RequiredTools"/> list is empty. The
/// bundle is prompt-only.
/// </para>
/// </remarks>
public interface ISkillBundleResolver
{
    /// <summary>
    /// Resolves the bundle identified by <paramref name="reference"/>.
    /// </summary>
    /// <param name="reference">Package + skill coordinates.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The resolved bundle.</returns>
    Task<SkillBundle> ResolveAsync(
        SkillBundleReference reference,
        CancellationToken cancellationToken = default);
}