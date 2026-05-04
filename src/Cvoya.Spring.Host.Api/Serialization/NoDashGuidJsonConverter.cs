// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Serialization;

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Identifiers;

/// <summary>
/// Lenient-on-parse <see cref="JsonConverter{T}"/> for <see cref="Guid"/>
/// fields on the public API surface (#1629 PR5).
///
/// <para>
/// <b>Wire-form decision.</b> The platform has two canonical formats for
/// Guid identifiers:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>URL paths and <see cref="Cvoya.Spring.Core.Messaging.Address"/>
///     strings</b> — 32-char no-dash lowercase hex
///     (<see cref="GuidFormatter.Format"/>). This is the form the
///     directory, message router, and route templates use; emitting
///     dashes there would force every callsite to round-trip through
///     <see cref="Guid"/> just to render a URL.
///   </description></item>
///   <item><description>
///     <b>JSON DTO bodies</b> — the standard dashed
///     <c>8-4-4-4-12</c> hex form (matching the OpenAPI <c>uuid</c>
///     format). This is what <see cref="System.Text.Json"/>,
///     <see cref="System.Text.Json.Utf8JsonReader.GetGuid"/>, the
///     Kiota-generated client's <c>GetGuidValue()</c>, and every common
///     OpenAPI-derived client expect by default. Emitting no-dash here
///     would break Kiota deserialisation
///     (<c>Utf8JsonReader.GetGuid</c> rejects the no-dash form) and
///     force every contract test to register a custom converter.
///   </description></item>
/// </list>
///
/// <para>
/// This converter is the JSON-body half of that contract. Emit follows
/// the standard dashed form for interop; parse stays lenient and accepts
/// both forms via <see cref="GuidFormatter.TryParse"/> so tools that
/// copy-paste no-dash ids from URLs / log lines continue to work.
/// </para>
///
/// <para>
/// OpenAPI note: <c>Microsoft.AspNetCore.OpenApi</c> advertises
/// <see cref="Guid"/>-typed fields as
/// <c>{ "type": "string", "format": "uuid" }</c>; with this converter
/// the wire shape matches the strict v4 form the format implies.
/// </para>
/// </summary>
public sealed class NoDashGuidJsonConverter : JsonConverter<Guid>
{
    /// <inheritdoc />
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Expected a string Guid token, got {reader.TokenType}.");
        }

        var raw = reader.GetString();
        if (!GuidFormatter.TryParse(raw, out var value))
        {
            throw new JsonException(
                $"Value '{raw}' is not a valid Guid. Expected the dashed 8-4-4-4-12 form (canonical for JSON bodies) or the 32-character no-dash form (canonical for URL paths and Address strings).");
        }

        return value;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
    {
        // Emit the dashed form so STJ-default consumers (Kiota client,
        // contract tests, openapi-typescript) deserialise without a
        // custom converter. The no-dash form remains canonical for URL
        // paths and Address strings — see GuidFormatter and the
        // type-level remarks above.
        writer.WriteStringValue(value.ToString("D"));
    }
}

/// <summary>
/// Companion converter for <c>Guid?</c>. Null on input deserialises to
/// <c>null</c>; null in C# emits a JSON <c>null</c>. Non-null values
/// share the dashed-emit / lenient-parse contract of
/// <see cref="NoDashGuidJsonConverter"/>.
/// </summary>
public sealed class NullableNoDashGuidJsonConverter : JsonConverter<Guid?>
{
    /// <inheritdoc />
    public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Expected a string Guid token or null, got {reader.TokenType}.");
        }

        var raw = reader.GetString();
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (!GuidFormatter.TryParse(raw, out var value))
        {
            throw new JsonException(
                $"Value '{raw}' is not a valid Guid. Expected the dashed 8-4-4-4-12 form (canonical for JSON bodies) or the 32-character no-dash form (canonical for URL paths and Address strings).");
        }

        return value;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value.ToString("D"));
    }
}