// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Skills;

/// <summary>
/// Wire-shape converter for <see cref="ToolDefinition"/>. Emits only
/// <c>name</c>, <c>description</c>, and <c>inputSchema</c> — the
/// <see cref="ToolDefinition.Namespace"/> property is computed from the
/// id and is not part of the wire surface (it would otherwise drift from
/// <see cref="ToolDefinition.Name"/> on the consumer side).
/// </summary>
/// <remarks>
/// The reader path supports the same shape so a round-trip through
/// <c>image_tools</c> in the database produces equivalent records.
/// </remarks>
internal sealed class ToolDefinitionConverter : JsonConverter<ToolDefinition>
{
    public override ToolDefinition Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected JSON object for ToolDefinition.");
        }

        string? name = null;
        string? description = null;
        JsonElement? inputSchema = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected property name in ToolDefinition object.");
            }

            var prop = reader.GetString();
            reader.Read();
            switch (prop)
            {
                case "name":
                    name = reader.GetString();
                    break;
                case "description":
                    description = reader.GetString();
                    break;
                case "inputSchema":
                    inputSchema = JsonElement.ParseValue(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (name is null || description is null || inputSchema is null)
        {
            throw new JsonException(
                "ToolDefinition wire payload must include name, description, and inputSchema.");
        }

        // The wire shape carries no category — SDK-side tools deserialised
        // from a remote agent's payload surface as uncategorised (empty
        // string) and are not enumerated by category-aware discovery.
        return new ToolDefinition(name, description, inputSchema.Value, string.Empty);
    }

    public override void Write(
        Utf8JsonWriter writer,
        ToolDefinition value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        writer.WriteString("description", value.Description);
        writer.WritePropertyName("inputSchema");
        value.InputSchema.WriteTo(writer);
        writer.WriteEndObject();
    }
}
