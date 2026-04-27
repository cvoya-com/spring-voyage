// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// HTTP-level integration tests for <c>/api/v1/units/{id}/boundary</c>
/// (#413). Exercises the GET / PUT / DELETE endpoints end-to-end against
/// <see cref="CustomWebApplicationFactory"/> with an in-memory boundary
/// store double so we can assert persistence behaviour without standing up
/// a Dapr actor host.
/// </summary>
public class BoundaryEndpointsTests : IClassFixture<BoundaryEndpointsTests.BoundaryEndpointsFactory>
{
    private readonly BoundaryEndpointsFactory _factory;
    private readonly HttpClient _client;

    public BoundaryEndpointsTests(BoundaryEndpointsFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetBoundary_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync("/api/v1/tenant/units/ghost/boundary", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetBoundary_NoBoundaryPersisted_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);
        _factory.BoundaryStore.GetAsync(
            Arg.Is<Address>(a => a.Path == unitName),
            Arg.Any<CancellationToken>()).Returns(UnitBoundary.Empty);

        var response = await _client.GetAsync($"/api/v1/tenant/units/{unitName}/boundary", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<UnitBoundaryResponse>(cancellationToken: ct);
        body.ShouldNotBeNull();
        body!.Opacities.ShouldBeNull();
        body.Projections.ShouldBeNull();
        body.Syntheses.ShouldBeNull();
    }

    [Fact]
    public async Task PutBoundary_AllRuleTypes_PersistsAndReturnsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);

        UnitBoundary? captured = null;
        _factory.BoundaryStore
            .SetAsync(Arg.Any<Address>(), Arg.Any<UnitBoundary>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.ArgAt<UnitBoundary>(1);
                return Task.CompletedTask;
            });
        _factory.BoundaryStore.GetAsync(
            Arg.Is<Address>(a => a.Path == unitName),
            Arg.Any<CancellationToken>())
            .Returns(_ => captured ?? UnitBoundary.Empty);

        var putBody = new UnitBoundaryResponse(
            Opacities: new[] { new BoundaryOpacityRuleDto("internal-*", null) },
            Projections: new[]
            {
                new BoundaryProjectionRuleDto("python/*", null, "backend-apis", "public-facing", "advanced"),
            },
            Syntheses: new[]
            {
                new BoundarySynthesisRuleDto("team-react", "react", null, "team aggregate", "advanced"),
            });

        var putResponse = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitName}/boundary", putBody, ct);
        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var stored = await _client.GetFromJsonAsync<UnitBoundaryResponse>(
            $"/api/v1/tenant/units/{unitName}/boundary", ct);
        stored!.Opacities!.ShouldHaveSingleItem().DomainPattern.ShouldBe("internal-*");
        stored.Projections!.ShouldHaveSingleItem().RenameTo.ShouldBe("backend-apis");
        stored.Syntheses!.ShouldHaveSingleItem().Name.ShouldBe("team-react");

        captured.ShouldNotBeNull();
        captured!.Opacities!.ShouldHaveSingleItem();
        captured.Projections!.ShouldHaveSingleItem().OverrideLevel.ShouldBe(ExpertiseLevel.Advanced);
    }

    [Fact]
    public async Task DeleteBoundary_KnownUnit_Returns204()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);

        var response = await _client.DeleteAsync($"/api/v1/tenant/units/{unitName}/boundary", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await _factory.BoundaryStore.Received().SetAsync(
            Arg.Is<Address>(a => a.Path == unitName),
            Arg.Is<UnitBoundary>(b => b.IsEmpty),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteBoundary_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.DeleteAsync("/api/v1/tenant/units/ghost/boundary", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static string NewUnitName() => $"eng-{Guid.NewGuid():N}";

    private void ArrangeResolved(string unitName)
    {
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == unitName),
                Arg.Any<CancellationToken>())
            .Returns(_ => new DirectoryEntry(
                new Address("unit", unitName),
                $"actor-{unitName}",
                "Engineering",
                "Engineering unit",
                null,
                DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Web-application factory subclass that swaps the DI-registered
    /// <see cref="IUnitBoundaryStore"/> for an NSubstitute double so these
    /// tests can assert persistence behaviour without wiring actor state.
    /// </summary>
    public sealed class BoundaryEndpointsFactory : CustomWebApplicationFactory
    {
        public IUnitBoundaryStore BoundaryStore { get; } = Substitute.For<IUnitBoundaryStore>();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(IUnitBoundaryStore))
                    .ToList();
                foreach (var descriptor in existing)
                {
                    services.Remove(descriptor);
                }
                services.AddSingleton(BoundaryStore);
            });
        }
    }
}