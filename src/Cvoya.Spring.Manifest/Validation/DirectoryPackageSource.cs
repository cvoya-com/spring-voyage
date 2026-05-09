// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Validation;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Concrete <see cref="IPackageSource"/> backed by an on-disk package
/// directory. The directory must contain <c>package.yaml</c> at the root —
/// the validator surfaces a clear diagnostic if it does not.
/// </summary>
public sealed class DirectoryPackageSource : IPackageSource
{
    private readonly string _rootPath;

    /// <summary>
    /// Creates a new <see cref="DirectoryPackageSource"/> rooted at
    /// <paramref name="rootPath"/>.
    /// </summary>
    public DirectoryPackageSource(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    /// <summary>The absolute root path of the package on disk.</summary>
    public string RootPath => _rootPath;

    /// <inheritdoc />
    public Task<string> ReadTextAsync(string relativePath, CancellationToken ct = default)
        => File.ReadAllTextAsync(Path.Combine(_rootPath, relativePath), ct);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string subdirectory, string pattern)
    {
        var fullDir = Path.Combine(_rootPath, subdirectory);
        if (!Directory.Exists(fullDir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(fullDir, pattern, SearchOption.TopDirectoryOnly)
            .Select(full => Path.GetRelativePath(_rootPath, full).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public bool FileExists(string relativePath)
        => File.Exists(Path.Combine(_rootPath, relativePath));
}
