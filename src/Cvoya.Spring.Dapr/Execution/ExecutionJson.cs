// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Collections.Generic;
using System.Text.Json;

using Cvoya.Spring.Core.Catalog;

/// <summary>
/// Read/write helpers for the canonical persisted <c>execution:</c> JSON
/// block (ADR-0038 amendment, #2634). The block carries exactly
/// <c>(runtime, model{provider, id}, image, hosting)</c>; <c>model</c> is
/// a nested object, not a flat string. Centralised here so every store
/// and the definition provider read and write the same shape.
/// </summary>
internal static class ExecutionJson
{
    /// <summary>
    /// Reads the structured <c>model: {provider, id}</c> object from an
    /// <c>execution</c> JSON element. Returns <c>null</c> when the
    /// <c>model</c> key is absent, is not an object, or is missing either
    /// of its two required string members.
    /// </summary>
    internal static Model? ReadModel(JsonElement exec)
    {
        if (!exec.TryGetProperty("model", out var model)
            || model.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var provider = GetStringOrNull(model, "provider");
        var id = GetStringOrNull(model, "id");
        if (provider is null || id is null)
        {
            return null;
        }

        return new Model(provider, id);
    }

    /// <summary>
    /// Writes the structured <c>model</c> object into the supplied block
    /// dictionary when <paramref name="model"/> is non-null.
    /// </summary>
    internal static void WriteModel(IDictionary<string, object?> block, Model? model)
    {
        if (model is null)
        {
            return;
        }

        block["model"] = new Dictionary<string, object?>
        {
            ["provider"] = model.Provider,
            ["id"] = model.Id,
        };
    }

    /// <summary>
    /// Returns a trimmed non-empty string property, or <c>null</c>.
    /// </summary>
    internal static string? GetStringOrNull(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var value = prop.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
