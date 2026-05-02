// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System;
using System.Collections.Generic;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// A union of "bare reference" and "inline body" for a <c>unit:</c> or
/// <c>agent:</c> slot in <see cref="PackageManifest"/>. The same YAML key
/// accepts either:
/// <list type="bullet">
///   <item><description>
///     A scalar string — interpreted as a reference (bare = within-package,
///     qualified = cross-package; see <see cref="ArtefactReference"/>).
///   </description></item>
///   <item><description>
///     A mapping — interpreted as an inline artefact definition. The body is
///     captured verbatim and re-emitted under the kind's root key (<c>unit:</c>
///     for a <see cref="PackageKind.UnitPackage"/>, <c>agent:</c> for an
///     <see cref="PackageKind.AgentPackage"/>) so the install activator can
///     consume it through the same path as a reference resolved from disk.
///   </description></item>
/// </list>
/// </summary>
/// <remarks>
/// Inline bodies are by construction self-contained — they live entirely in
/// the uploaded <c>package.yaml</c> — so they do not trigger upload-mode local
/// reference rejection (ADR-0035 decision 13). The wizard install path uses
/// this to submit a single self-contained YAML through the same install
/// pipeline as the CLI (ADR-0035 decision 6).
/// </remarks>
public sealed class InlineArtefactDefinition
{
    /// <summary>
    /// The reference string when the slot is a bare reference; <c>null</c>
    /// when the slot carries an inline body.
    /// </summary>
    public string? Reference { get; }

    /// <summary>
    /// The inline body's artefact name (for reference-uniqueness and
    /// diagnostic output). Pulled from the body's <c>id</c> field when
    /// present, falling back to <c>name</c>. <c>null</c> when the slot is a
    /// bare reference.
    /// </summary>
    public string? InlineName { get; }

    /// <summary>
    /// The captured inline YAML body, serialised as a flow / block mapping
    /// suitable for re-wrapping under the kind root key. <c>null</c> when the
    /// slot is a bare reference.
    /// </summary>
    public string? InlineBody { get; }

    private InlineArtefactDefinition(string? reference, string? inlineName, string? inlineBody)
    {
        Reference = reference;
        InlineName = inlineName;
        InlineBody = inlineBody;
    }

    /// <summary><c>true</c> when this value carries an inline body rather than a reference.</summary>
    public bool IsInline => InlineBody is not null;

    /// <summary>Builds a definition that holds a bare reference.</summary>
    public static InlineArtefactDefinition FromReference(string reference)
        => new(reference, inlineName: null, inlineBody: null);

    /// <summary>Builds a definition that holds an inline body.</summary>
    public static InlineArtefactDefinition FromInline(string inlineName, string inlineBody)
        => new(reference: null, inlineName, inlineBody);
}

/// <summary>
/// YamlDotNet type converter for <see cref="InlineArtefactDefinition"/>.
/// Accepts either a scalar string or a mapping at the same YAML key. Public
/// so consumers re-emitting a <see cref="PackageManifest"/> (e.g. round-trip
/// tests, export tooling) can register the same converter on their
/// <c>SerializerBuilder</c>.
/// </summary>
public sealed class InlineArtefactDefinitionYamlConverter : IYamlTypeConverter
{
    public bool Accepts(Type type) => type == typeof(InlineArtefactDefinition);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            // Bare reference (a single string).
            return InlineArtefactDefinition.FromReference(scalar.Value);
        }

        if (parser.Current is MappingStart)
        {
            // Inline body: deserialize as a generic dictionary, derive a name
            // from `id` / `name`, and re-serialize the body to a YAML string
            // so the install activator can consume it.
            var body = (Dictionary<object, object?>?)rootDeserializer(typeof(Dictionary<object, object?>))
                ?? new Dictionary<object, object?>();

            var inlineName = ExtractInlineName(body);
            var inlineYaml = SerializeBody(body);
            return InlineArtefactDefinition.FromInline(inlineName, inlineYaml);
        }

        throw new YamlException(
            parser.Current?.Start ?? Mark.Empty,
            parser.Current?.End ?? Mark.Empty,
            "Expected a scalar reference or a mapping (inline definition) for this artefact slot.");
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is not InlineArtefactDefinition def)
        {
            // Null slot — emit a null scalar.
            emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, string.Empty, ScalarStyle.Plain, true, false));
            return;
        }

        if (def.Reference is not null)
        {
            // Bare reference — emit as a scalar string.
            emitter.Emit(new Scalar(
                AnchorName.Empty,
                TagName.Empty,
                def.Reference,
                ScalarStyle.Any,
                isPlainImplicit: true,
                isQuotedImplicit: false));
            return;
        }

        // Inline body — re-emit the captured YAML body verbatim. The body is
        // already a valid YAML mapping document, so we parse it back into a
        // node graph and emit each event so it nests correctly under the
        // current key.
        if (def.InlineBody is not null)
        {
            var parser = new Parser(new System.IO.StringReader(def.InlineBody));
            // Discard StreamStart / DocumentStart wrappers; emit the inner
            // mapping events into the outer stream.
            while (parser.MoveNext())
            {
                var ev = parser.Current!;
                if (ev is StreamStart or DocumentStart or DocumentEnd or StreamEnd)
                {
                    continue;
                }
                emitter.Emit(ev);
            }
        }
    }

    private static string ExtractInlineName(Dictionary<object, object?> body)
    {
        // Prefer 'id' (the agent wizard sets both 'id' and 'name'); fall back
        // to 'name' (units use 'name' as the canonical identifier).
        if (body.TryGetValue("id", out var id) && id is string idStr && !string.IsNullOrWhiteSpace(idStr))
        {
            return idStr;
        }
        if (body.TryGetValue("name", out var name) && name is string nameStr && !string.IsNullOrWhiteSpace(nameStr))
        {
            return nameStr;
        }
        // No identifier in the body — we still produce a synthetic name so
        // reference-uniqueness checks have something to key on. The activator
        // will surface the missing-name error when the body is parsed.
        return "<inline>";
    }

    private static string SerializeBody(Dictionary<object, object?> body)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .Build();
        return serializer.Serialize(body);
    }
}