// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.ErrorHandling;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

using Cvoya.Spring.Cli.Generated.Models;

using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

/// <summary>
/// Parses and renders the structured ADR-0039 multi-parent inheritance
/// conflict response. Kept separate from the generic exception renderer so
/// command sites that need the B7/B8 one-line-per-field shape can opt in
/// without changing every ProblemDetails failure.
/// </summary>
internal static class MultiParentInheritanceConflictFormatter
{
    private const string ConflictCode = "MultiParentInheritanceConflict";

    public static bool TryParse(ApiException exception, out MultiParentInheritanceConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(exception);

        conflict = MultiParentInheritanceConflict.Empty;
        if (exception is not ProblemDetails problem
            || problem.AdditionalData is not { Count: > 0 } data)
        {
            return false;
        }

        if (!TryGet(data, "error", out var error)
            || !string.Equals(ReadScalar(error), ConflictCode, StringComparison.Ordinal))
        {
            return false;
        }

        if (!TryGet(data, "conflictingFields", out var fieldsRaw))
        {
            return false;
        }

        var fields = ReadFields(fieldsRaw);
        if (fields.Count == 0)
        {
            return false;
        }

        conflict = new MultiParentInheritanceConflict(fields);
        return true;
    }

    public static IReadOnlyList<string> FormatLines(
        MultiParentInheritanceConflict conflict,
        IReadOnlyDictionary<string, string>? unitLabels = null)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        var lines = new List<string>(conflict.Fields.Count);
        foreach (var field in conflict.Fields)
        {
            var values = field.Values
                .Select(v => $"{ResolveLabel(v.UnitId, unitLabels)}={v.Value}")
                .ToArray();
            lines.Add($"{field.Name}: {string.Join(", ", values)}");
        }
        return lines;
    }

    private static string ResolveLabel(string unitId, IReadOnlyDictionary<string, string>? unitLabels)
    {
        if (unitLabels is not null
            && unitLabels.TryGetValue(unitId, out var label)
            && !string.IsNullOrWhiteSpace(label))
        {
            return label;
        }
        return unitId;
    }

    private static IReadOnlyList<MultiParentInheritanceConflictField> ReadFields(object raw)
        => raw switch
        {
            JsonElement element => ReadFields(element),
            UntypedObject obj => ReadFields(obj.GetValue()),
            IDictionary<string, UntypedNode> map => ReadFields(map),
            IDictionary<string, object> map => ReadFields(map),
            _ => Array.Empty<MultiParentInheritanceConflictField>(),
        };

    private static IReadOnlyList<MultiParentInheritanceConflictField> ReadFields(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<MultiParentInheritanceConflictField>();
        }

        var fields = new List<MultiParentInheritanceConflictField>();
        foreach (var property in element.EnumerateObject())
        {
            var values = ReadValues(property.Value);
            if (values.Count > 0)
            {
                fields.Add(new MultiParentInheritanceConflictField(property.Name, values));
            }
        }
        return fields;
    }

    private static IReadOnlyList<MultiParentInheritanceConflictField> ReadFields(
        IDictionary<string, UntypedNode> map)
    {
        var fields = new List<MultiParentInheritanceConflictField>();
        foreach (var (field, rawValues) in map)
        {
            var values = ReadValues(rawValues);
            if (values.Count > 0)
            {
                fields.Add(new MultiParentInheritanceConflictField(field, values));
            }
        }
        return fields;
    }

    private static IReadOnlyList<MultiParentInheritanceConflictField> ReadFields(
        IDictionary<string, object> map)
    {
        var fields = new List<MultiParentInheritanceConflictField>();
        foreach (var (field, rawValues) in map)
        {
            var values = ReadValues(rawValues);
            if (values.Count > 0)
            {
                fields.Add(new MultiParentInheritanceConflictField(field, values));
            }
        }
        return fields;
    }

    private static IReadOnlyList<MultiParentInheritanceConflictValue> ReadValues(object raw)
        => raw switch
        {
            JsonElement element => ReadValues(element),
            UntypedArray array => ReadValues(array.GetValue()),
            IEnumerable<UntypedNode> nodes => ReadValues(nodes),
            IEnumerable<object> objects => ReadValues(objects),
            _ => Array.Empty<MultiParentInheritanceConflictValue>(),
        };

    private static IReadOnlyList<MultiParentInheritanceConflictValue> ReadValues(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<MultiParentInheritanceConflictValue>();
        }

        var values = new List<MultiParentInheritanceConflictValue>();
        foreach (var item in element.EnumerateArray())
        {
            if (TryReadValue(item, out var value))
            {
                values.Add(value);
            }
        }
        return values;
    }

    private static IReadOnlyList<MultiParentInheritanceConflictValue> ReadValues(
        IEnumerable<UntypedNode> nodes)
    {
        var values = new List<MultiParentInheritanceConflictValue>();
        foreach (var node in nodes)
        {
            if (TryReadValue(node, out var value))
            {
                values.Add(value);
            }
        }
        return values;
    }

    private static IReadOnlyList<MultiParentInheritanceConflictValue> ReadValues(
        IEnumerable<object> objects)
    {
        var values = new List<MultiParentInheritanceConflictValue>();
        foreach (var obj in objects)
        {
            if (TryReadValue(obj, out var value))
            {
                values.Add(value);
            }
        }
        return values;
    }

    private static bool TryReadValue(JsonElement element, out MultiParentInheritanceConflictValue value)
    {
        value = default!;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var unitId = TryGetPropertyScalar(element, "unitId")
            ?? TryGetPropertyScalar(element, "source");
        var parentValue = TryGetPropertyScalar(element, "value");
        if (string.IsNullOrWhiteSpace(unitId) || string.IsNullOrWhiteSpace(parentValue))
        {
            return false;
        }

        value = new MultiParentInheritanceConflictValue(unitId!, parentValue!);
        return true;
    }

    private static bool TryReadValue(UntypedNode node, out MultiParentInheritanceConflictValue value)
    {
        value = default!;
        if (node is not UntypedObject obj)
        {
            return false;
        }

        var map = obj.GetValue();
        var unitId = TryGet(map, "unitId", out var unitNode)
            ? ReadScalar(unitNode)
            : TryGet(map, "source", out var sourceNode)
                ? ReadScalar(sourceNode)
                : null;
        var parentValue = TryGet(map, "value", out var valueNode)
            ? ReadScalar(valueNode)
            : null;

        if (string.IsNullOrWhiteSpace(unitId) || string.IsNullOrWhiteSpace(parentValue))
        {
            return false;
        }

        value = new MultiParentInheritanceConflictValue(unitId!, parentValue!);
        return true;
    }

    private static bool TryReadValue(object obj, out MultiParentInheritanceConflictValue value)
    {
        value = default!;
        return obj switch
        {
            JsonElement element => TryReadValue(element, out value),
            UntypedNode node => TryReadValue(node, out value),
            IDictionary<string, object> map => TryReadValue(map, out value),
            _ => false,
        };
    }

    private static bool TryReadValue(
        IDictionary<string, object> map,
        out MultiParentInheritanceConflictValue value)
    {
        value = default!;
        var unitId = TryGet(map, "unitId", out var unitRaw)
            ? ReadScalar(unitRaw)
            : TryGet(map, "source", out var sourceRaw)
                ? ReadScalar(sourceRaw)
                : null;
        var parentValue = TryGet(map, "value", out var valueRaw)
            ? ReadScalar(valueRaw)
            : null;

        if (string.IsNullOrWhiteSpace(unitId) || string.IsNullOrWhiteSpace(parentValue))
        {
            return false;
        }

        value = new MultiParentInheritanceConflictValue(unitId!, parentValue!);
        return true;
    }

    private static string? TryGetPropertyScalar(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }
        return ReadScalar(property);
    }

    private static string? ReadScalar(object? raw)
        => raw switch
        {
            null => null,
            string s => s,
            JsonElement element => ReadScalar(element),
            UntypedString s => s.GetValue(),
            UntypedBoolean b => b.GetValue() ? "true" : "false",
            UntypedInteger i => i.GetValue().ToString(CultureInfo.InvariantCulture),
            UntypedLong l => l.GetValue().ToString(CultureInfo.InvariantCulture),
            UntypedDouble d => d.GetValue().ToString(CultureInfo.InvariantCulture),
            UntypedFloat f => f.GetValue().ToString(CultureInfo.InvariantCulture),
            UntypedDecimal d => d.GetValue().ToString(CultureInfo.InvariantCulture),
            UntypedNull => null,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => raw.ToString(),
        };

    private static string? ReadScalar(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => null,
        };

    private static bool TryGet<T>(IDictionary<string, T> map, string key, out T value)
    {
        if (map.TryGetValue(key, out value!))
        {
            return true;
        }

        foreach (var (candidate, candidateValue) in map)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                value = candidateValue;
                return true;
            }
        }

        value = default!;
        return false;
    }
}

internal sealed record MultiParentInheritanceConflict(
    IReadOnlyList<MultiParentInheritanceConflictField> Fields)
{
    public static readonly MultiParentInheritanceConflict Empty = new(Array.Empty<MultiParentInheritanceConflictField>());

    public IReadOnlyList<string> UnitIds
        => Fields
            .SelectMany(f => f.Values)
            .Select(v => v.UnitId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}

internal sealed record MultiParentInheritanceConflictField(
    string Name,
    IReadOnlyList<MultiParentInheritanceConflictValue> Values);

internal sealed record MultiParentInheritanceConflictValue(string UnitId, string Value);