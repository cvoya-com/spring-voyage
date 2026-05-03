// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests.TestHelpers;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Maps a stable slug literal (the legacy human-readable test identity such
/// as "ada" / "test-sender") to a deterministic Guid so tests that previously
/// passed slug strings into <see cref="Cvoya.Spring.Core.Messaging.Address.For"/>
/// keep their cross-call referential identity now that addresses require a
/// real Guid id.
/// </summary>
/// <remarks>
/// Two different slugs produce two different Guids; the same slug always
/// produces the same Guid (via SHA-1 of the slug, with the variant + version
/// bits massaged so the value is a valid RFC 4122 v5-style Guid). The
/// returned <see cref="HexFor"/> form is the canonical 32-character no-dash
/// hex string that <c>Address.For</c> expects.
/// </remarks>
public static class TestSlugIds
{
    private static readonly ConcurrentDictionary<string, Guid> Cache = new();

    /// <summary>Deterministic Guid for the given slug.</summary>
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

    /// <summary>Canonical 32-character no-dash hex form of <see cref="For"/>.</summary>
    public static string HexFor(string slug) => For(slug).ToString("N");
}