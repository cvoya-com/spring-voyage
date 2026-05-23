// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Canonicalises the content of an <see cref="AgentBootstrapBundle"/> and
/// derives its content-addressable sha256 hash (ADR-0055 §3).
/// </summary>
/// <remarks>
/// <para>
/// The hash MUST be deterministic for identical content. The canonical
/// form is a UTF-8 byte stream built directly here rather than going
/// through <c>JsonSerializer</c>:
/// </para>
/// <list type="number">
///   <item>Each file contributes <c>path\0sha256\0content\0</c> bytes (UTF-8).</item>
///   <item>Files are sorted by <see cref="AgentBootstrapFile.Path"/> using ordinal comparison.</item>
///   <item>Each platform-file-hash entry contributes <c>path\0hash\0</c> bytes (UTF-8).</item>
///   <item>Platform-file-hash entries are sorted by key using ordinal comparison.</item>
/// </list>
/// <para>
/// The <c>NUL</c> separator is unambiguous (UTF-8 never produces a NUL byte
/// inside any character) and avoids the framing pitfalls of length-prefixed
/// or whitespace-canonicalised JSON. <see cref="AgentBootstrapBundle.Version"/>
/// and <see cref="AgentBootstrapBundle.IssuedAt"/> are deliberately excluded
/// — the hash is computed first, the bundle is stamped with it after.
/// </para>
/// </remarks>
public static class AgentBootstrapBundleHasher
{
    /// <summary>
    /// Computes the canonical content hash of <paramref name="files"/> and
    /// <paramref name="platformFileHashes"/>. The returned string has the
    /// form <c>sha256:&lt;lowercase-hex&gt;</c>.
    /// </summary>
    public static string Compute(
        IReadOnlyList<AgentBootstrapFile> files,
        IReadOnlyDictionary<string, string> platformFileHashes)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(platformFileHashes);

        using var sha = SHA256.Create();
        using var stream = new MemoryStream();

        foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            WriteField(stream, file.Path);
            WriteField(stream, file.Sha256);
            WriteField(stream, file.Content);
        }

        foreach (var pair in platformFileHashes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            WriteField(stream, pair.Key);
            WriteField(stream, pair.Value);
        }

        stream.Position = 0;
        var digest = sha.ComputeHash(stream);
        return "sha256:" + Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Computes <c>sha256:&lt;hex&gt;</c> of <paramref name="content"/>'s
    /// UTF-8 bytes. Used by bundle providers to populate
    /// <see cref="AgentBootstrapFile.Sha256"/> per file.
    /// </summary>
    public static string ComputeFileHash(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = Encoding.UTF8.GetBytes(content);
        var digest = SHA256.HashData(bytes);
        return "sha256:" + Convert.ToHexStringLower(digest);
    }

    private static void WriteField(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte(0);
    }
}
