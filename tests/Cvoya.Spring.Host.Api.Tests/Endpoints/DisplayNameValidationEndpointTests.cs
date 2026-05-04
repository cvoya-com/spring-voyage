// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Validation;
using Cvoya.Spring.Host.Api.Models;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end coverage for issue #1632 — every entity create / update
/// surface that accepts a <c>display_name</c> must reject Guid-shaped
/// values with a 400 ProblemDetails carrying the structured error code
/// in the <c>code</c> extension.
///
/// <para>
/// The tests deliberately cover one Guid form per endpoint (rather than
/// fanning all five forms across every endpoint) — the validator's
/// own unit tests in
/// <see cref="Cvoya.Spring.Core.Tests.Validation.DisplayNameValidatorTests"/>
/// already prove every form is rejected; the integration tests just have
/// to prove the wiring is in place.
/// </para>
/// </summary>
public class DisplayNameValidationEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    // Sample dashed Guid we feed into requests as a display_name. The
    // string is intentionally a syntactically valid Guid so the validator
    // sees the collision class — the tests do not care whether it ever
    // mapped to a real entity.
    private const string GuidShapedDashed = "8c5fab2a-8e7e-4b9c-92f1-d8a3b4c5d6e7";
    private const string GuidShapedNoDash = "8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DisplayNameValidationEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateAgent_GuidShapedDisplayName_Returns400WithStructuredCode()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();

        // unitIds / connector resolution must NOT have been called — the
        // validator runs before any of that work.
        var request = new CreateAgentRequest(
            Name: Guid.NewGuid().ToString("N"),
            DisplayName: GuidShapedDashed,
            Description: "",
            Role: null,
            UnitIds: new[] { Guid.NewGuid().ToString("N") });

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/agents", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertProblemCarriesCodeAsync(response, DisplayNameValidator.GuidShapeErrorCode, ct);

        // Validator runs before any persistence work — the directory must
        // not have been touched.
        await _factory.DirectoryService.DidNotReceive().RegisterAsync(
            Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAgent_EmptyDisplayName_Returns400WithStructuredCode()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();

        var request = new CreateAgentRequest(
            Name: Guid.NewGuid().ToString("N"),
            DisplayName: "   ",
            Description: "",
            Role: null,
            UnitIds: new[] { Guid.NewGuid().ToString("N") });

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/agents", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertProblemCarriesCodeAsync(response, DisplayNameValidator.EmptyErrorCode, ct);
    }

    [Fact]
    public async Task CreateUnit_GuidShapedDisplayName_Returns400WithStructuredCode()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();

        var request = new CreateUnitRequest(
            Name: Guid.NewGuid().ToString("N"),
            DisplayName: GuidShapedNoDash,
            Description: "",
            IsTopLevel: true);

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/units", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertProblemCarriesCodeAsync(response, DisplayNameValidator.GuidShapeErrorCode, ct);

        // Validator runs before the creation service — the directory must
        // not have been touched.
        await _factory.DirectoryService.DidNotReceive().RegisterAsync(
            Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchUnit_GuidShapedDisplayName_Returns400WithStructuredCode()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();

        // Wire up a resolvable unit so the 400 must come from the validator,
        // not from the unit-not-found path.
        var unitGuid = Guid.NewGuid();
        var unitId = unitGuid.ToString("N");
        var entry = new DirectoryEntry(
            new Address("unit", unitGuid),
            unitGuid,
            "Engineering",
            "Engineering unit",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == unitGuid), Arg.Any<CancellationToken>())
            .Returns(entry);

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/tenant/units/{unitId}",
            new UpdateUnitRequest(DisplayName: GuidShapedDashed),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertProblemCarriesCodeAsync(response, DisplayNameValidator.GuidShapeErrorCode, ct);

        // Validator runs before the directory write — UpdateEntryAsync
        // must not have been touched.
        await _factory.DirectoryService.DidNotReceive().UpdateEntryAsync(
            Arg.Any<Address>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchUnit_NullDisplayName_StaysOutOfValidator()
    {
        // The PATCH contract treats null DisplayName as "leave unchanged" —
        // the validator must NOT fire on the missing field, so a body that
        // only updates Model still succeeds.
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();

        var unitGuid = Guid.NewGuid();
        var unitId = unitGuid.ToString("N");
        var entry = new DirectoryEntry(
            new Address("unit", unitGuid),
            unitGuid,
            "Engineering",
            "Engineering unit",
            null,
            DateTimeOffset.UtcNow);
        _factory.DirectoryService
            .ResolveAsync(Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == unitGuid), Arg.Any<CancellationToken>())
            .Returns(entry);

        var proxy = Substitute.For<Cvoya.Spring.Dapr.Actors.IUnitActor>();
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new Cvoya.Spring.Core.Units.UnitMetadata(null, null, "gpt-4o", null));
        proxy.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Cvoya.Spring.Core.Units.UnitStatus.Draft);
        _factory.ActorProxyFactory
            .CreateActorProxy<Cvoya.Spring.Dapr.Actors.IUnitActor>(Arg.Any<global::Dapr.Actors.ActorId>(), Arg.Any<string>())
            .Returns(proxy);

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/tenant/units/{unitId}",
            new UpdateUnitRequest(Model: "gpt-4o"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CreateTenant_GuidShapedDisplayName_Returns400WithStructuredCode()
    {
        var ct = TestContext.Current.CancellationToken;

        var request = new CreateTenantRequest(
            Id: Guid.NewGuid().ToString("N"),
            DisplayName: GuidShapedDashed);

        var response = await _client.PostAsJsonAsync("/api/v1/platform/tenants", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertProblemCarriesCodeAsync(response, DisplayNameValidator.GuidShapeErrorCode, ct);
    }

    [Fact]
    public async Task UpdateTenant_GuidShapedDisplayName_Returns400WithStructuredCode()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed a tenant first so the 400 must come from the validator, not
        // from the tenant-not-found path.
        var tenantId = Guid.NewGuid().ToString("N");
        var create = await _client.PostAsJsonAsync(
            "/api/v1/platform/tenants",
            new CreateTenantRequest(tenantId, "Original"),
            ct);
        create.StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/platform/tenants/{tenantId}",
            new UpdateTenantRequest(GuidShapedNoDash),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertProblemCarriesCodeAsync(response, DisplayNameValidator.GuidShapeErrorCode, ct);
    }

    private static async Task AssertProblemCarriesCodeAsync(
        HttpResponseMessage response,
        string expectedCode,
        CancellationToken ct)
    {
        // The endpoint surfaces the structured code under the ProblemDetails
        // `code` extension. We assert on the JSON shape directly so the
        // contract that the CLI / portal pattern-match against stays
        // load-bearing under tests.
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("code", out var codeElement).ShouldBeTrue(
            $"problem-details body did not include a `code` extension: {body}");
        codeElement.GetString().ShouldBe(expectedCode);
        doc.RootElement.GetProperty("status").GetInt32().ShouldBe(400);
    }
}