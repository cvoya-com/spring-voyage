// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Output;

using System.Text;
using System.Text.Json;

/// <summary>
/// Formats JSON data as aligned tables or raw JSON for CLI output.
/// </summary>
public static class OutputFormatter
{
    /// <summary>
    /// Formats a JSON array as an aligned table with the specified columns.
    /// If the data is a single object, it is treated as a one-row table.
    /// </summary>
    public static string FormatTable(JsonElement data, string[] columns)
    {
        var rows = new List<string[]>();

        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                rows.Add(ExtractRow(item, columns));
            }
        }
        else if (data.ValueKind == JsonValueKind.Object)
        {
            rows.Add(ExtractRow(data, columns));
        }

        if (rows.Count == 0)
        {
            return "No results found.";
        }

        // Calculate column widths
        var widths = new int[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            widths[i] = columns[i].Length;
        }

        foreach (var row in rows)
        {
            for (var i = 0; i < columns.Length; i++)
            {
                if (row[i].Length > widths[i])
                {
                    widths[i] = row[i].Length;
                }
            }
        }

        var sb = new StringBuilder();

        // Header
        for (var i = 0; i < columns.Length; i++)
        {
            if (i > 0)
            {
                sb.Append("  ");
            }
            sb.Append(columns[i].ToUpperInvariant().PadRight(widths[i]));
        }
        sb.AppendLine();

        // Separator
        for (var i = 0; i < columns.Length; i++)
        {
            if (i > 0)
            {
                sb.Append("  ");
            }
            sb.Append(new string('-', widths[i]));
        }
        sb.AppendLine();

        // Rows
        foreach (var row in rows)
        {
            for (var i = 0; i < columns.Length; i++)
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

    /// <summary>
    /// Formats a JSON element as indented JSON.
    /// </summary>
    public static string FormatJson(JsonElement data)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(data, options);
    }

    private static string[] ExtractRow(JsonElement item, string[] columns)
    {
        var row = new string[columns.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            if (item.TryGetProperty(columns[i], out var value))
            {
                row[i] = value.ValueKind == JsonValueKind.Null ? "" : value.ToString() ?? "";
            }
            else
            {
                row[i] = "";
            }
        }
        return row;
    }
}
