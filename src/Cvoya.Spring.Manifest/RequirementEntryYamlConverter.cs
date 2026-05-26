// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System;
using System.Collections.Generic;
using System.Linq;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

/// <summary>
/// YamlDotNet type converter for <see cref="RequirementEntry"/>. Each
/// <c>requires:</c> list entry is a YAML mapping whose first key is the
/// requirement type (<c>connector</c>, future: <c>secret</c>,
/// <c>capability</c>) and whose value is the type-specific binding
/// identifier as a scalar (ADR-0037 decision 3). The mapping may carry
/// additional connector-type-specific sibling keys that pre-seed the
/// install wizard's binding form; the only one defined today is
/// <c>labels:</c> on <c>connector:</c> entries (issue #2780).
/// </summary>
public sealed class RequirementEntryYamlConverter : IYamlTypeConverter
{
    private static readonly HashSet<string> KnownSiblingKeys =
        new(StringComparer.Ordinal) { "labels" };

    /// <inheritdoc />
    public bool Accepts(Type type) => type == typeof(RequirementEntry);

    /// <inheritdoc />
    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.Current is not MappingStart)
        {
            throw new YamlException(
                parser.Current?.Start ?? Mark.Empty,
                parser.Current?.End ?? Mark.Empty,
                "Expected a mapping for a requires entry, e.g. '- connector: github'.");
        }

        parser.Consume<MappingStart>();

        if (!parser.TryConsume<Scalar>(out var keyScalar))
        {
            throw new YamlException(
                parser.Current?.Start ?? Mark.Empty,
                parser.Current?.End ?? Mark.Empty,
                "Requires entry's first child must be a scalar discriminator (connector).");
        }

        var typeName = keyScalar.Value;
        if (!TryParseType(typeName, out var requirementType))
        {
            throw new YamlException(
                keyScalar.Start, keyScalar.End,
                $"Unknown requirement type '{typeName}'. Expected one of: connector.");
        }

        if (!parser.TryConsume<Scalar>(out var valueScalar))
        {
            throw new YamlException(
                parser.Current?.Start ?? Mark.Empty,
                parser.Current?.End ?? Mark.Empty,
                $"Requires entry '{typeName}': expected a scalar binding identifier.");
        }

        RequirementLabelsBlock? labels = null;
        while (parser.Current is not MappingEnd)
        {
            if (!parser.TryConsume<Scalar>(out var siblingKey))
            {
                throw new YamlException(
                    parser.Current?.Start ?? Mark.Empty,
                    parser.Current?.End ?? Mark.Empty,
                    $"Requires entry '{typeName}': expected a sibling field name or end of mapping.");
            }
            var siblingName = siblingKey.Value;
            if (!KnownSiblingKeys.Contains(siblingName))
            {
                throw new YamlException(
                    siblingKey.Start, siblingKey.End,
                    $"Requires entry '{typeName}': unknown sibling field '{siblingName}'. " +
                    $"Expected one of: {string.Join(", ", KnownSiblingKeys)}.");
            }

            if (siblingName == "labels")
            {
                if (labels is not null)
                {
                    throw new YamlException(
                        siblingKey.Start, siblingKey.End,
                        $"Requires entry '{typeName}': 'labels' declared more than once.");
                }
                labels = ParseLabels(parser);
            }
        }

        parser.Consume<MappingEnd>();

        return new RequirementEntry
        {
            Type = requirementType,
            Identifier = valueScalar.Value ?? string.Empty,
            Labels = labels,
        };
    }

    /// <inheritdoc />
    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is not RequirementEntry entry)
        {
            return;
        }

        emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, isImplicit: true, MappingStyle.Block));
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, TypeToYamlKey(entry.Type),
            ScalarStyle.Plain, isPlainImplicit: true, isQuotedImplicit: false));
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, entry.Identifier,
            ScalarStyle.Plain, isPlainImplicit: true, isQuotedImplicit: false));

        if (entry.Labels is { IsEmpty: false } labels)
        {
            EmitLabels(emitter, labels);
        }

        emitter.Emit(new MappingEnd());
    }

    private static RequirementLabelsBlock ParseLabels(IParser parser)
    {
        if (parser.Current is not MappingStart)
        {
            throw new YamlException(
                parser.Current?.Start ?? Mark.Empty,
                parser.Current?.End ?? Mark.Empty,
                "Expected a mapping for 'labels' (with 'include:' / 'exclude:' lists).");
        }
        parser.Consume<MappingStart>();

        List<string>? include = null;
        List<string>? exclude = null;
        while (parser.Current is not MappingEnd)
        {
            if (!parser.TryConsume<Scalar>(out var key))
            {
                throw new YamlException(
                    parser.Current?.Start ?? Mark.Empty,
                    parser.Current?.End ?? Mark.Empty,
                    "Expected a key inside 'labels:'.");
            }
            switch (key.Value)
            {
                case "include":
                    if (include is not null)
                    {
                        throw new YamlException(
                            key.Start, key.End,
                            "'labels.include' declared more than once.");
                    }
                    include = ConsumeStringList(parser, "labels.include");
                    break;
                case "exclude":
                    if (exclude is not null)
                    {
                        throw new YamlException(
                            key.Start, key.End,
                            "'labels.exclude' declared more than once.");
                    }
                    exclude = ConsumeStringList(parser, "labels.exclude");
                    break;
                default:
                    throw new YamlException(
                        key.Start, key.End,
                        $"Unknown 'labels' sub-key '{key.Value}'. Expected 'include' or 'exclude'.");
            }
        }
        parser.Consume<MappingEnd>();

        return new RequirementLabelsBlock
        {
            Include = include?.ToArray() ?? System.Array.Empty<string>(),
            Exclude = exclude?.ToArray() ?? System.Array.Empty<string>(),
        };
    }

    private static List<string> ConsumeStringList(IParser parser, string contextLabel)
    {
        if (parser.Current is not SequenceStart)
        {
            throw new YamlException(
                parser.Current?.Start ?? Mark.Empty,
                parser.Current?.End ?? Mark.Empty,
                $"Expected a sequence for '{contextLabel}'.");
        }
        parser.Consume<SequenceStart>();

        var result = new List<string>();
        while (parser.Current is not SequenceEnd)
        {
            if (!parser.TryConsume<Scalar>(out var item))
            {
                throw new YamlException(
                    parser.Current?.Start ?? Mark.Empty,
                    parser.Current?.End ?? Mark.Empty,
                    $"'{contextLabel}': expected a string item.");
            }
            var trimmed = item.Value?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                result.Add(trimmed);
            }
        }
        parser.Consume<SequenceEnd>();
        return result;
    }

    private static void EmitLabels(IEmitter emitter, RequirementLabelsBlock labels)
    {
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, "labels",
            ScalarStyle.Plain, isPlainImplicit: true, isQuotedImplicit: false));
        emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, isImplicit: true, MappingStyle.Block));

        if (labels.Include.Count > 0)
        {
            EmitStringList(emitter, "include", labels.Include);
        }
        if (labels.Exclude.Count > 0)
        {
            EmitStringList(emitter, "exclude", labels.Exclude);
        }

        emitter.Emit(new MappingEnd());
    }

    private static void EmitStringList(IEmitter emitter, string key, IReadOnlyList<string> items)
    {
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, key,
            ScalarStyle.Plain, isPlainImplicit: true, isQuotedImplicit: false));
        emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Block));
        foreach (var item in items)
        {
            emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, item,
                ScalarStyle.Plain, isPlainImplicit: true, isQuotedImplicit: false));
        }
        emitter.Emit(new SequenceEnd());
    }

    private static bool TryParseType(string? key, out RequirementType requirementType)
    {
        switch (key?.Trim().ToLowerInvariant())
        {
            case "connector":
                requirementType = RequirementType.Connector;
                return true;
            default:
                requirementType = default;
                return false;
        }
    }

    private static string TypeToYamlKey(RequirementType requirementType) => requirementType switch
    {
        RequirementType.Connector => "connector",
        _ => throw new ArgumentOutOfRangeException(nameof(requirementType), requirementType, "Unknown requirement type."),
    };
}
