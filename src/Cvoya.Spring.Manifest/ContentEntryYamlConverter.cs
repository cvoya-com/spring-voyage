// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

/// <summary>
/// YamlDotNet type converter for <see cref="ContentEntry"/>. Each
/// <c>content:</c> list entry is a single-key YAML mapping whose key is
/// the artefact discriminator (<c>unit</c>, <c>agent</c>, <c>skill</c>,
/// <c>workflow</c>) and whose value is either a scalar reference or — for
/// <c>unit</c> / <c>agent</c> only — an inline mapping body. The shape is
/// intentionally idiomatic with the existing <c>members:</c> grammar
/// (<c>members: - unit: foo</c>) so authors don't have to learn a second
/// nesting style for the same concept.
/// </summary>
/// <remarks>
/// Inline bodies for <c>skill</c> / <c>workflow</c> are not supported by
/// the v0.1 grammar — those artefact kinds resolve only through file /
/// directory references. The converter still tolerates the shape on
/// the read side (it deserialises whatever it sees) but the parser's
/// <see cref="PackageManifestParser"/> rejects an inline body on a
/// non-unit / non-agent entry with an actionable message.
/// </remarks>
public sealed class ContentEntryYamlConverter : IYamlTypeConverter
{
    /// <inheritdoc />
    public bool Accepts(Type type) => type == typeof(ContentEntry);

    /// <inheritdoc />
    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.Current is not MappingStart)
        {
            throw new YamlException(
                parser.Current?.Start ?? Mark.Empty,
                parser.Current?.End ?? Mark.Empty,
                "Expected a single-key mapping for a content entry, e.g. '- unit: my-unit'.");
        }

        parser.Consume<MappingStart>();

        // Read the discriminator key (unit / agent / skill / workflow).
        if (!parser.TryConsume<Scalar>(out var keyScalar))
        {
            throw new YamlException(
                parser.Current?.Start ?? Mark.Empty,
                parser.Current?.End ?? Mark.Empty,
                "Content entry's first child must be a scalar discriminator (unit, agent, skill, workflow).");
        }

        var kindName = keyScalar.Value;
        if (!TryParseKind(kindName, out var artefactKind))
        {
            throw new YamlException(
                keyScalar.Start, keyScalar.End,
                $"Unknown content entry kind '{kindName}'. Expected one of: unit, agent, skill, workflow.");
        }

        // Read the value — scalar (reference) or mapping (inline body).
        InlineArtefactDefinition definition;
        if (parser.TryConsume<Scalar>(out var valueScalar))
        {
            definition = InlineArtefactDefinition.FromReference(valueScalar.Value ?? string.Empty);
        }
        else if (parser.Current is MappingStart)
        {
            // Inline body. Reuse the InlineArtefactDefinition path so we
            // capture the same name-extraction + body-serialisation
            // behaviour the wizard already relies on.
            var inlineConverter = new InlineArtefactDefinitionYamlConverter();
            definition = (InlineArtefactDefinition)inlineConverter.ReadYaml(
                parser, typeof(InlineArtefactDefinition), rootDeserializer)!;
        }
        else
        {
            throw new YamlException(
                parser.Current?.Start ?? Mark.Empty,
                parser.Current?.End ?? Mark.Empty,
                $"Content entry '{kindName}': expected a scalar reference or a mapping (inline body).");
        }

        // Consume MappingEnd. Anything else here means the content entry
        // had more than the single discriminator key — reject so authors
        // see the breakage immediately rather than silently dropping fields.
        if (parser.Current is not MappingEnd)
        {
            throw new YamlException(
                parser.Current?.Start ?? Mark.Empty,
                parser.Current?.End ?? Mark.Empty,
                $"Content entry '{kindName}' must be a single-key mapping; saw additional fields. " +
                "Each content entry carries exactly one discriminator (unit, agent, skill, workflow).");
        }
        parser.Consume<MappingEnd>();

        return new ContentEntry
        {
            Kind = artefactKind,
            Definition = definition,
        };
    }

    /// <inheritdoc />
    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is not ContentEntry entry)
        {
            return;
        }

        emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, isImplicit: true, MappingStyle.Block));
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, KindToYamlKey(entry.Kind),
            ScalarStyle.Plain, isPlainImplicit: true, isQuotedImplicit: false));

        var inlineConverter = new InlineArtefactDefinitionYamlConverter();
        inlineConverter.WriteYaml(emitter, entry.Definition, typeof(InlineArtefactDefinition), serializer);

        emitter.Emit(new MappingEnd());
    }

    private static bool TryParseKind(string? key, out ArtefactKind kind)
    {
        switch (key?.Trim().ToLowerInvariant())
        {
            case "unit":
                kind = ArtefactKind.Unit;
                return true;
            case "agent":
                kind = ArtefactKind.Agent;
                return true;
            case "skill":
                kind = ArtefactKind.Skill;
                return true;
            case "workflow":
                kind = ArtefactKind.Workflow;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static string KindToYamlKey(ArtefactKind kind) => kind switch
    {
        ArtefactKind.Unit => "unit",
        ArtefactKind.Agent => "agent",
        ArtefactKind.Skill => "skill",
        ArtefactKind.Workflow => "workflow",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown artefact kind."),
    };
}
