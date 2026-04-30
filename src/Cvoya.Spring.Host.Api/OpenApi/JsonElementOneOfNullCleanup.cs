// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.OpenApi;

using Microsoft.OpenApi;

/// <summary>
/// Document transformer that rewrites the broken
/// <c>oneOf: [{ "type": "null" }, { "$ref": "#/components/schemas/JsonElement" }]</c>
/// shape that .NET 10's native OpenAPI generator emits for <c>JsonElement?</c>
/// properties (and request bodies) into a single <c>$ref</c> to
/// <c>JsonElement</c> (#1254).
/// </summary>
/// <remarks>
/// <para>
/// The generator declares the <c>JsonElement</c> component schema as
/// <c>{}</c> — the empty schema, which under JSON Schema 2020-12 matches
/// <em>any</em> instance, including <c>null</c>. Combining that with
/// <c>oneOf:[null, JsonElement]</c> means a <c>null</c> instance matches
/// <em>both</em> branches; <c>oneOf</c> requires exactly one match, so the
/// schema rejects valid wire data. Strict OpenAPI 3.1 / JSON Schema
/// 2020-12 evaluators (Kiota, OpenAPI Generator, JsonSchema.Net used by the
/// C1.3 contract tests) hit this immediately. Runtimes that fall back to
/// looser AnyOf-style semantics (Swagger UI, the .NET dev-time validator)
/// don't, which is why the bug shipped — see #1254 for the surfacing case.
/// </para>
/// <para>
/// We pick "drop the oneOf wrapper" (Option A in the issue) over "tighten
/// JsonElement to reject null" (Option B):
/// </para>
/// <list type="bullet">
///   <item><description>
///     Smaller blast radius — only the broken property slots change shape.
///     <c>JsonElement</c> stays the universal "any JSON value" schema, which
///     is what other call sites (e.g. config-schema endpoints that return a
///     concrete schema body) want.
///   </description></item>
///   <item><description>
///     Self-describing — the bare <c>$ref</c> reads as "any JSON value or
///     null" without leaking JSON Schema arithmetic into the property
///     declaration.
///   </description></item>
///   <item><description>
///     Existing contract waivers in <c>MessageContractTests</c> /
///     <c>ThreadContractTests</c> documented the <c>oneOf:[null, JsonElement]</c>
///     shape as the bug; matching that exact diagnosis keeps the fix
///     traceable.
///   </description></item>
/// </list>
/// <para>
/// Implemented as a document transformer (rather than a per-schema
/// transformer) because the broken shape appears both as a property of a
/// component schema (<c>MessageResponse.responsePayload</c> &amp; friends)
/// and as a top-level inline schema on request-body slots (e.g.
/// <c>POST /api/v1/connectors/{slugOrId}/bind</c>'s body). A document
/// walker handles both in one pass and naturally extends to nested
/// <c>oneOf</c>/<c>anyOf</c>/<c>allOf</c>/<c>items</c>/<c>additionalProperties</c>.
/// </para>
/// </remarks>
internal static class JsonElementOneOfNullCleanup
{
    /// <summary>
    /// The component-schema id that triggers cleanup. The .NET 10 OpenAPI
    /// generator names the <see cref="System.Text.Json.JsonElement"/>
    /// component schema this; if it ever picks a different id, the fix
    /// silently no-ops (the bad shape persists), so a contract test guards
    /// the assumption.
    /// </summary>
    public const string TargetSchemaId = "JsonElement";

    /// <summary>
    /// Walks the document and replaces every parent slot whose value is a
    /// schema of the form <c>oneOf: [null, $ref to JsonElement]</c> with a
    /// direct <c>$ref</c> to <see cref="TargetSchemaId"/>.
    /// </summary>
    public static void Apply(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Components?.Schemas is { } componentSchemas)
        {
            // Snapshot the keys — we mutate values in-place via the
            // dictionary indexer rather than the keys themselves, but a
            // snapshot avoids "collection modified during enumeration"
            // surprises if the API decides to grow.
            foreach (var key in componentSchemas.Keys.ToArray())
            {
                var normalized = NormalizeSchema(componentSchemas[key], document);
                componentSchemas[key] = normalized;
                if (normalized is OpenApiSchema concrete)
                {
                    WalkSchema(concrete, document);
                }
            }
        }

        if (document.Paths is { } paths)
        {
            foreach (var pathItem in paths.Values)
            {
                if (pathItem.Operations is null) continue;
                foreach (var operation in pathItem.Operations.Values)
                {
                    WalkOperation(operation, document);
                }
            }
        }
    }

    private static void WalkOperation(OpenApiOperation operation, OpenApiDocument document)
    {
        if (operation.RequestBody?.Content is { } requestContent)
        {
            foreach (var media in requestContent.Values)
            {
                NormalizeMediaSchema(media, document);
            }
        }

        if (operation.Responses is { } responses)
        {
            foreach (var response in responses.Values)
            {
                if (response.Content is null) continue;
                foreach (var media in response.Content.Values)
                {
                    NormalizeMediaSchema(media, document);
                }
            }
        }

        if (operation.Parameters is { } parameters)
        {
            foreach (var parameter in parameters)
            {
                if (parameter.Schema is null) continue;
                // Schema setters live on the concrete classes, not on the
                // IOpenApi* interfaces (which expose getters only). Skip
                // anything that isn't the framework-emitted concrete type;
                // the only call sites we need to mutate are the .NET 10
                // generator's output, which always materialises concretes.
                // Normalising parameter schemas is mostly belt-and-braces;
                // query parameters virtually never carry a JsonElement,
                // but the walk is cheap and future-proofs the surface.
                if (parameter is OpenApiParameter concreteParameter)
                {
                    concreteParameter.Schema = NormalizeSchema(parameter.Schema, document);
                    if (concreteParameter.Schema is OpenApiSchema concrete)
                    {
                        WalkSchema(concrete, document);
                    }
                }
                else if (parameter.Schema is OpenApiSchema concrete)
                {
                    WalkSchema(concrete, document);
                }
            }
        }
    }

    private static void NormalizeMediaSchema(OpenApiMediaType media, OpenApiDocument document)
    {
        if (media.Schema is null) return;
        // Microsoft.OpenApi v2.x exposes Content as
        // IDictionary<string, OpenApiMediaType> — the value is the
        // concrete class, not an interface, so we can write Schema directly.
        media.Schema = NormalizeSchema(media.Schema, document);
        if (media.Schema is OpenApiSchema concreteSchema)
        {
            WalkSchema(concreteSchema, document);
        }
    }

    private static void WalkSchema(OpenApiSchema schema, OpenApiDocument document)
    {
        if (schema.Properties is { } properties)
        {
            foreach (var key in properties.Keys.ToArray())
            {
                var normalized = NormalizeSchema(properties[key], document);
                properties[key] = normalized;
                if (normalized is OpenApiSchema concrete)
                {
                    WalkSchema(concrete, document);
                }
            }
        }

        if (schema.Items is not null)
        {
            var normalizedItems = NormalizeSchema(schema.Items, document);
            schema.Items = normalizedItems;
            if (normalizedItems is OpenApiSchema concrete)
            {
                WalkSchema(concrete, document);
            }
        }

        if (schema.AdditionalProperties is not null)
        {
            var normalizedAdditional = NormalizeSchema(schema.AdditionalProperties, document);
            schema.AdditionalProperties = normalizedAdditional;
            if (normalizedAdditional is OpenApiSchema concrete)
            {
                WalkSchema(concrete, document);
            }
        }

        NormalizeComposition(schema.OneOf, document);
        NormalizeComposition(schema.AnyOf, document);
        NormalizeComposition(schema.AllOf, document);
    }

    private static void NormalizeComposition(IList<IOpenApiSchema>? composition, OpenApiDocument document)
    {
        if (composition is null) return;
        for (var i = 0; i < composition.Count; i++)
        {
            var normalized = NormalizeSchema(composition[i], document);
            composition[i] = normalized;
            if (normalized is OpenApiSchema concrete)
            {
                WalkSchema(concrete, document);
            }
        }
    }

    /// <summary>
    /// If <paramref name="schema"/> is the broken <c>oneOf:[null, $ref to
    /// JsonElement]</c> shape, return a fresh <see cref="OpenApiSchemaReference"/>
    /// to the JsonElement component. Otherwise return the input unchanged
    /// so callers can blindly assign the result back into the parent slot.
    /// </summary>
    private static IOpenApiSchema NormalizeSchema(IOpenApiSchema schema, OpenApiDocument document)
    {
        if (schema is not OpenApiSchema concrete) return schema;
        if (concrete.OneOf is not { Count: 2 } oneOf) return schema;

        var hasNullBranch = false;
        var hasJsonElementRef = false;

        foreach (var branch in oneOf)
        {
            if (IsNullTypeBranch(branch))
            {
                hasNullBranch = true;
                continue;
            }
            if (IsJsonElementReference(branch))
            {
                hasJsonElementRef = true;
                continue;
            }
            // Any other branch shape — leave the schema alone. We only
            // rewrite the exact two-branch pattern the issue documents;
            // a oneOf with more shape (e.g. {null, RefA, RefB}) is a real
            // discriminated union and not what we're after.
            return schema;
        }

        if (!hasNullBranch || !hasJsonElementRef) return schema;

        return new OpenApiSchemaReference(TargetSchemaId, document);
    }

    private static bool IsNullTypeBranch(IOpenApiSchema branch)
    {
        if (branch is not OpenApiSchema concrete) return false;
        // The .NET 10 generator emits the null branch as `{ "type": "null" }`
        // — Type is a flags enum, so a single-flag value of Null is the
        // expected shape. Anything richer (e.g. `{ "type": "null", "title": "x" }`
        // — same flag set, plus other keywords) we conservatively skip:
        // the pattern we're rewriting is the bare-null shape only.
        if (concrete.Type != JsonSchemaType.Null) return false;
        if (concrete.OneOf is { Count: > 0 }) return false;
        if (concrete.AnyOf is { Count: > 0 }) return false;
        if (concrete.AllOf is { Count: > 0 }) return false;
        if (concrete.Properties is { Count: > 0 }) return false;
        if (concrete.Items is not null) return false;
        return true;
    }

    private static bool IsJsonElementReference(IOpenApiSchema branch)
    {
        if (branch is not OpenApiSchemaReference reference) return false;
        return string.Equals(
            reference.Reference?.Id,
            TargetSchemaId,
            StringComparison.Ordinal);
    }
}