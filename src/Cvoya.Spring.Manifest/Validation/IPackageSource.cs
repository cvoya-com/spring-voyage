// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Validation;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A read-only view of a package's contents that the offline
/// <see cref="PackageValidator"/> drives. Shipped as a seam so the v0.1
/// implementation works against an on-disk directory
/// (<see cref="DirectoryPackageSource"/>) and a follow-up archive / tarball
/// implementation can drop in without re-shaping the validator.
/// </summary>
/// <remarks>
/// All paths are <em>relative to the package root</em> (the directory that
/// contains <c>package.yaml</c>) and use forward slashes. A
/// <see cref="DirectoryPackageSource"/> normalises path separators on the way
/// out so a Windows host emits the same diagnostic <c>file:</c> values as Linux
/// / macOS.
/// </remarks>
public interface IPackageSource
{
    /// <summary>
    /// Reads the text content of the file at <paramref name="relativePath"/>.
    /// Throws if the file does not exist.
    /// </summary>
    Task<string> ReadTextAsync(string relativePath, CancellationToken ct = default);

    /// <summary>
    /// Returns relative paths (forward-slash) of files matching the supplied
    /// glob <paramref name="pattern"/> directly under
    /// <paramref name="subdirectory"/>. The subdirectory not existing is not
    /// an error — the validator emits no diagnostics for an empty section.
    /// </summary>
    IEnumerable<string> EnumerateFiles(string subdirectory, string pattern);

    /// <summary>
    /// Returns <c>true</c> when a file exists at <paramref name="relativePath"/>.
    /// </summary>
    bool FileExists(string relativePath);
}
