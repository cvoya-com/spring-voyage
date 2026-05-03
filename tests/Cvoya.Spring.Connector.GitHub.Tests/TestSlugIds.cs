// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Maps a stable slug literal to a deterministic Guid so tests that
/// previously passed slug strings into
/// <see cref="Cvoya.Spring.Core.Messaging.Address.For"/> keep their
/// cross-call referential identity now that addresses require a Guid id.
/// </summary>
public static class TestSlugIds
{
    private static readonly ConcurrentDictionary<string, Guid> Cache = new();

    public static Guid For(string slug)
    {
        if (slug is null)
        {
            throw new ArgumentNullException(nameof(slug));
        }

        return Cache.GetOrAdd(slug, static s =>
        {
            var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s));
            Span<byte> guidBytes = stackalloc byte[16];
            bytes.AsSpan(0, 16).CopyTo(guidBytes);

            guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
            guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

            (guidBytes[0], guidBytes[3]) = (guidBytes[3], guidBytes[0]);
            (guidBytes[1], guidBytes[2]) = (guidBytes[2], guidBytes[1]);
            (guidBytes[4], guidBytes[5]) = (guidBytes[5], guidBytes[4]);
            (guidBytes[6], guidBytes[7]) = (guidBytes[7], guidBytes[6]);
            return new Guid(guidBytes);
        });
    }

    public static string HexFor(string slug) => For(slug).ToString("N");
}