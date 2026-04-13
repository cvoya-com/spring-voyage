// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Output;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Serialization.Json;

/// <summary>
/// Renders typed results from the Kiota client as aligned tables or wire-format JSON.
/// </summary>
public static class OutputFormatter
{
    /// <summary>
    /// A column descriptor for <see cref="FormatTable{T}"/>: a header label and an
    /// accessor that returns the cell value for a given row (null/empty becomes blank).
    /// </summary>
    public readonly record struct Column<T>(string Header, Func<T, string?> Get);

    /// <summary>
    /// Renders <paramref name="rows"/> as an aligned ASCII table with the given columns.
    /// Returns "No results found." when the sequence is empty.
    /// </summary>
    public static string FormatTable<T>(IEnumerable<T> rows, IReadOnlyList<Column<T>> columns)
    {
        var values = rows
            .Select(row => columns.Select(c => c.Get(row) ?? string.Empty).ToArray())
            .ToList();

        if (values.Count == 0)
        {
            return "No results found.";
        }

        var widths = new int[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            widths[i] = columns[i].Header.Length;
        }
        foreach (var row in values)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (row[i].Length > widths[i])
                {
                    widths[i] = row[i].Length;
                }
            }
        }

        var sb = new StringBuilder();

        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("  ");
            }
            sb.Append(columns[i].Header.ToUpperInvariant().PadRight(widths[i]));
        }
        sb.AppendLine();

        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("  ");
            }
            sb.Append(new string('-', widths[i]));
        }
        sb.AppendLine();

        foreach (var row in values)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append("  ");
                }
                sb.Append(row[i].PadRight(widths[i]));
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Renders a single row as a one-row table.</summary>
    public static string FormatTable<T>(T row, IReadOnlyList<Column<T>> columns)
        => FormatTable(new[] { row }, columns);

    // One writer factory is enough — all FormatJson overloads share it. Instantiating
    // our own factory sidesteps Kiota's global SerializationWriterFactoryRegistry,
    // which is populated lazily by the SpringApiKiotaClient constructor and therefore
    // leaks test-ordering dependencies (see CI run on #189 where this test ran before
    // any client was constructed and failed with "no factory registered").
    private static readonly JsonSerializationWriterFactory JsonWriterFactory = new();

    /// <summary>
    /// Serialises a Kiota <see cref="IParsable"/> model as wire-format JSON. Uses Kiota's
    /// own JSON writer so property names match the OpenAPI contract (camelCase) rather
    /// than the C# PascalCase that <c>System.Text.Json</c> would emit.
    /// </summary>
    public static string FormatJson<T>(T value) where T : IParsable
    {
        using var writer = JsonWriterFactory.GetSerializationWriter("application/json");
        writer.WriteObjectValue(null, value);
        using var stream = writer.GetSerializedContent();
        return ReadIndented(stream);
    }

    /// <summary>Serialises a sequence of Kiota models as a wire-format JSON array.</summary>
    public static string FormatJson<T>(IEnumerable<T> values) where T : IParsable
    {
        using var writer = JsonWriterFactory.GetSerializationWriter("application/json");
        writer.WriteCollectionOfObjectValues(null, values);
        using var stream = writer.GetSerializedContent();
        return ReadIndented(stream);
    }

    private static string ReadIndented(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var raw = reader.ReadToEnd();
        // Kiota emits compact JSON; reindent for human readability.
        using var doc = System.Text.Json.JsonDocument.Parse(raw);
        return System.Text.Json.JsonSerializer.Serialize(
            doc.RootElement,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}