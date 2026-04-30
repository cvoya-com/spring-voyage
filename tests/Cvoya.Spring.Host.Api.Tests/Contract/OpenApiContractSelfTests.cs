// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Text.Json;

using Shouldly;

using Xunit;

/// <summary>
/// Self-tests for the <see cref="OpenApiContract"/> helper itself. Without
/// these, a regression where every call to <c>AssertResponse</c> silently
/// passes (e.g., a broken schema lookup that returns the always-true empty
/// schema) would go undetected and the rest of the contract suite would
/// give a false sense of safety. The tests prove that:
/// <list type="number">
/// <item>A body that violates the spec actually fails.</item>
/// <item>A body for an undeclared status / media type fails fast with a clear error.</item>
/// </list>
/// </summary>
public class OpenApiContractSelfTests
{
    [Fact]
    public void AssertResponse_BodyMissingRequiredField_Throws()
    {
        // /api/v1/auth/me returns UserProfileResponse which requires both
        // userId and displayName. A body missing one MUST fail the
        // contract — otherwise the whole suite is an empty assertion.
        var bodyMissingField = """{"userId":"alice"}""";

        Should.Throw<ContractAssertionException>(() =>
            OpenApiContract.AssertResponse(
                "/api/v1/tenant/auth/me", "get", "200", bodyMissingField));
    }

    [Fact]
    public void AssertResponse_UndeclaredMediaType_Throws()
    {
        // application/xml is not declared anywhere — surfacing a clear
        // error here is better than silently passing.
        var anyBody = """{"userId":"alice","displayName":"Alice"}""";

        Should.Throw<ContractAssertionException>(() =>
            OpenApiContract.AssertResponse(
                "/api/v1/tenant/auth/me", "get", "200", anyBody, "application/xml"));
    }

    [Fact]
    public void AssertResponse_UndeclaredStatusCode_Throws()
    {
        // The /me endpoint declares 200 + 401; 418 is undeclared.
        var anyBody = """{"userId":"alice","displayName":"Alice"}""";

        Should.Throw<ContractAssertionException>(() =>
            OpenApiContract.AssertResponse(
                "/api/v1/tenant/auth/me", "get", "418", anyBody));
    }

    [Fact]
    public void AssertResponse_ProblemDetailsMissingStatus_StillPasses_BecauseNothingIsRequired()
    {
        // Regression guard for the ProblemDetails schema in particular —
        // every property is optional (no `required` on the schema), so an
        // empty object {} is a valid problem+json instance. Confirms the
        // validator isn't conjuring required-ness out of nowhere.
        var emptyProblem = "{}";

        // Use the auth-tokens 409 branch — it returns ProblemDetails on
        // problem+json. Should NOT throw.
        Should.NotThrow(() =>
            OpenApiContract.AssertResponse(
                "/api/v1/tenant/auth/tokens", "post", "409",
                emptyProblem, "application/problem+json"));
    }

    [Fact]
    public void OpenApiSpec_NullableJsonElementProperties_AreNotWrappedInBrokenOneOf()
    {
        // Regression guard for #1254. The .NET 10 OpenAPI generator
        // emits `JsonElement?` properties as
        // `oneOf:[{type:null}, {$ref:#/components/schemas/JsonElement}]`,
        // and the JsonElement component schema is `{}` (matches anything,
        // including null). Under strict JSON Schema 2020-12 evaluation a
        // null instance matches BOTH oneOf branches, so the schema rejects
        // valid wire data. JsonElementOneOfNullCleanup rewrites every such
        // slot to a bare `$ref`. This test scans the committed openapi.json
        // for any surviving instance of the bad shape and fails loudly so
        // a future generator regression cannot silently re-introduce the bug.
        using var doc = LoadOpenApi();
        var bad = new List<string>();
        ScanForBrokenJsonElementOneOf(doc.RootElement, path: "$", bad);
        bad.ShouldBeEmpty(
            "Found JsonElement+null oneOf wrappers at: " + string.Join(", ", bad));
    }

    private static JsonDocument LoadOpenApi()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "openapi.json");
        var bytes = File.ReadAllBytes(path);
        return JsonDocument.Parse(bytes);
    }

    private static void ScanForBrokenJsonElementOneOf(
        JsonElement element,
        string path,
        List<string> hits)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (IsBrokenJsonElementOneOf(element))
                {
                    hits.Add(path);
                }
                foreach (var prop in element.EnumerateObject())
                {
                    ScanForBrokenJsonElementOneOf(
                        prop.Value, $"{path}.{prop.Name}", hits);
                }
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    ScanForBrokenJsonElementOneOf(item, $"{path}[{i}]", hits);
                    i++;
                }
                break;
        }
    }

    private static bool IsBrokenJsonElementOneOf(JsonElement element)
    {
        if (!element.TryGetProperty("oneOf", out var oneOf)) return false;
        if (oneOf.ValueKind != JsonValueKind.Array) return false;
        if (oneOf.GetArrayLength() != 2) return false;

        var hasNull = false;
        var hasJsonElementRef = false;
        foreach (var branch in oneOf.EnumerateArray())
        {
            if (branch.ValueKind != JsonValueKind.Object) return false;
            if (branch.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "null"
                && branch.EnumerateObject().Count() == 1)
            {
                hasNull = true;
                continue;
            }
            if (branch.TryGetProperty("$ref", out var refValue)
                && refValue.ValueKind == JsonValueKind.String
                && refValue.GetString() == "#/components/schemas/JsonElement"
                && branch.EnumerateObject().Count() == 1)
            {
                hasJsonElementRef = true;
                continue;
            }
            return false;
        }
        return hasNull && hasJsonElementRef;
    }
}