// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

/// <summary>
/// YamlDotNet type converter for <see cref="RequirementEntry"/>. Each
/// <c>requires:</c> list entry is a single-key YAML mapping whose key is
/// the requirement type (<c>connector</c>, future: <c>secret</c>,
/// <c>capability</c>) and whose value is the type-specific binding
/// identifier as a scalar (ADR-0037 decision 3).
/// </summary>
public sealed class RequirementEntryYamlConverter : IYamlTypeConverter
{
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
                "Expected a single-key mapping for a requires entry, e.g. '- connector: github'.");
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

        if (parser.Current is not MappingEnd)
        {
            throw new YamlException(
                parser.Current?.Start ?? Mark.Empty,
                parser.Current?.End ?? Mark.Empty,
                $"Requires entry '{typeName}' must be a single-key mapping; saw additional fields. " +
                "Each requires entry carries exactly one discriminator (connector).");
        }
        parser.Consume<MappingEnd>();

        return new RequirementEntry
        {
            Type = requirementType,
            Identifier = valueScalar.Value ?? string.Empty,
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
        emitter.Emit(new MappingEnd());
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
