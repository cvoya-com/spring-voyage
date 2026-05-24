// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Threads;

using System.Text.Json;

using Cvoya.Spring.Dapr.Data.Entities;

/// <summary>
/// Helpers for reading and writing the
/// <see cref="ThreadEntity.ParticipantNameSnapshots"/> jsonb column
/// (#2533). Both the writer (<see cref="EfMessageWriter"/>) and the
/// read-side enrichment path serialise / deserialise through this type
/// so the on-disk shape (compact dictionary of <c>address →
/// display-name</c>) stays in one place.
///
/// <para>
/// A malformed payload (should never happen in normal operation — the
/// writer is the only producer) deserialises to an empty dictionary so
/// the read path falls back to the live resolver rather than throwing.
/// </para>
/// </summary>
internal static class ParticipantNameSnapshotJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Compact: no whitespace inside the persisted jsonb column. We
        // do not want the writer to perturb the payload size every time
        // it touches the column.
    };

    /// <summary>
    /// Empty payload — the migration's column default and the value
    /// callers should treat as "no snapshot yet". Exposed as a constant
    /// so tests and integration callers do not need to know the literal.
    /// </summary>
    public const string Empty = "{}";

    /// <summary>
    /// Parses the jsonb payload into a mutable dictionary the writer
    /// can upsert into. Uses ordinal address comparison — addresses are
    /// already lower-case via <see cref="Cvoya.Spring.Core.Identifiers.GuidFormatter"/>.
    /// </summary>
    public static Dictionary<string, string> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, SerializerOptions);
            if (parsed is null || parsed.Count == 0)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            // Re-wrap with the ordinal comparer the rest of the pipeline
            // assumes; the deserialiser produces the default comparer.
            return new Dictionary<string, string>(parsed, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Serialises the snapshot dictionary back to the compact jsonb form
    /// stored in the column.
    /// </summary>
    public static string Write(IReadOnlyDictionary<string, string> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return Empty;
        }

        return JsonSerializer.Serialize(snapshots, SerializerOptions);
    }
}
