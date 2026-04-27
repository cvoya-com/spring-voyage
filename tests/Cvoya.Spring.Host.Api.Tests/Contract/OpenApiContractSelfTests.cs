// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

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
}