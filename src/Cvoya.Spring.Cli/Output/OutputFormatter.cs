// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Output;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
    ///
    /// #1064: certain Kiota models (notably the <c>POST /messages</c>
    /// response, which carries an <c>UntypedNode</c> payload slot) trip
    /// the bundled <c>JsonSerializationWriter</c> with
    /// <c>InvalidOperationException: '}' is invalid following a property name</c>.
    /// Rather than fail the whole CLI invocation we fall back to
    /// <see cref="JsonSerializer"/> with the same casing/indentation; the
    /// resulting JSON is functionally equivalent for scripted consumers
    /// (camelCase, indented, no <c>additionalData</c> wrapping). Pass
    /// <paramref name="verbose"/> to surface the underlying writer error
    /// to stderr so operators can see why the fallback fired.
    /// </summary>
    public static string FormatJson<T>(T value, bool verbose = false) where T : IParsable
    {
        try
        {
            using var writer = JsonWriterFactory.GetSerializationWriter("application/json");
            writer.WriteObjectValue(null, value);
            using var stream = writer.GetSerializedContent();
            return ReadIndented(stream);
        }
        catch (Exception ex) when (IsRetryableSerializerFailure(ex))
        {
            if (verbose)
            {
                Console.Error.WriteLine(
                    $"warn: kiota serializer failed, fell back to System.Text.Json: {ex.Message}");
            }
            return JsonSerializer.Serialize(value, value?.GetType() ?? typeof(T), PlainJsonOptions);
        }
    }

    /// <summary>Serialises a sequence of Kiota models as a wire-format JSON array.</summary>
    public static string FormatJson<T>(IEnumerable<T> values, bool verbose = false) where T : IParsable
    {
        try
        {
            using var writer = JsonWriterFactory.GetSerializationWriter("application/json");
            writer.WriteCollectionOfObjectValues(null, values);
            using var stream = writer.GetSerializedContent();
            return ReadIndented(stream);
        }
        catch (Exception ex) when (IsRetryableSerializerFailure(ex))
        {
            if (verbose)
            {
                Console.Error.WriteLine(
                    $"warn: kiota serializer failed, fell back to System.Text.Json: {ex.Message}");
            }
            return JsonSerializer.Serialize(values, PlainJsonOptions);
        }
    }

    // The Kiota writer surfaces malformed-output errors as
    // InvalidOperationException from the underlying Utf8JsonWriter (see
    // #1064). Out-of-memory and cancellation are NOT retryable; we let
    // them bubble unchanged so the host can decide what to do.
    private static bool IsRetryableSerializerFailure(Exception ex)
        => ex is InvalidOperationException
            || ex is JsonException
            || ex is NotSupportedException;

    // System.Text.Json options shared by the plain-object overloads below. camelCase
    // keeps CLI JSON indistinguishable from the OpenAPI wire shape for consumers
    // that pipe `--output json` into jq or a scripting layer. Null values are
    // preserved so callers can tell "field is absent" from "field is null".
    private static readonly JsonSerializerOptions PlainJsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

    /// <summary>
    /// Serialises an arbitrary POCO (or POCO sequence) as camelCase JSON. Used by
    /// commands that emit a CLI-local shape (e.g. <c>unit members list</c>'s merged
    /// view of agent-scheme and unit-scheme members — the latter has no Kiota model
    /// because it doesn't come from a single typed endpoint).
    /// </summary>
    public static string FormatJsonPlain(object? value)
        => JsonSerializer.Serialize(value, PlainJsonOptions);

    private static string ReadIndented(Stream stream)
    {
        using var reader = new StreamReader(stream);
        var raw = reader.ReadToEnd();
        // Kiota emits compact JSON; reindent for human readability.
        using var doc = JsonDocument.Parse(raw);
        return JsonSerializer.Serialize(
            doc.RootElement,
            new JsonSerializerOptions { WriteIndented = true });
    }
}