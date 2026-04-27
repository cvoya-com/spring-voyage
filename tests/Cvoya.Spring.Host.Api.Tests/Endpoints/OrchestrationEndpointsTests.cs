// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// HTTP-level integration tests for <c>/api/v1/units/{id}/orchestration</c>
/// (#606). Exercises the GET / PUT / DELETE endpoints end-to-end against
/// <see cref="CustomWebApplicationFactory"/> with an
/// <see cref="IUnitOrchestrationStore"/> double so we assert persistence
/// and cache-invalidation wiring without needing a live Dapr actor host.
/// </summary>
public class OrchestrationEndpointsTests
    : IClassFixture<OrchestrationEndpointsTests.OrchestrationEndpointsFactory>
{
    private readonly OrchestrationEndpointsFactory _factory;
    private readonly HttpClient _client;

    public OrchestrationEndpointsTests(OrchestrationEndpointsFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetOrchestration_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync("/api/v1/tenant/units/ghost/orchestration", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetOrchestration_NoStrategyPersisted_ReturnsEmptyShape()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);
        _factory.OrchestrationStore
            .GetStrategyKeyAsync(unitName, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var response = await _client.GetAsync($"/api/v1/tenant/units/{unitName}/orchestration", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<UnitOrchestrationResponse>(cancellationToken: ct);
        body.ShouldNotBeNull();
        body!.Strategy.ShouldBeNull();
    }

    [Fact]
    public async Task PutOrchestration_ValidKey_PersistsAndReturnsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);

        string? captured = null;
        _factory.OrchestrationStore
            .SetStrategyKeyAsync(unitName, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.ArgAt<string?>(1);
                return Task.CompletedTask;
            });
        _factory.OrchestrationStore
            .GetStrategyKeyAsync(unitName, Arg.Any<CancellationToken>())
            .Returns(_ => captured);

        var putResponse = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitName}/orchestration",
            new UnitOrchestrationResponse("workflow"),
            ct);
        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await putResponse.Content.ReadFromJsonAsync<UnitOrchestrationResponse>(cancellationToken: ct);
        body!.Strategy.ShouldBe("workflow");
        captured.ShouldBe("workflow");
    }

    [Fact]
    public async Task PutOrchestration_EmptyStrategy_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);

        var putResponse = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitName}/orchestration",
            new UnitOrchestrationResponse(Strategy: ""),
            ct);
        putResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Scope the assertion to this test's unit so calls from sibling
        // tests sharing the class fixture cannot poison the expectation.
        await _factory.OrchestrationStore.DidNotReceive().SetStrategyKeyAsync(
            unitName,
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PutOrchestration_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var putResponse = await _client.PutAsJsonAsync(
            "/api/v1/tenant/units/ghost/orchestration",
            new UnitOrchestrationResponse("ai"),
            ct);
        putResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteOrchestration_KnownUnit_ReturnsNoContentAndClearsSlot()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitName = NewUnitName();
        ArrangeResolved(unitName);

        var response = await _client.DeleteAsync($"/api/v1/tenant/units/{unitName}/orchestration", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await _factory.OrchestrationStore.Received().SetStrategyKeyAsync(
            unitName,
            strategyKey: null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteOrchestration_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.DeleteAsync("/api/v1/tenant/units/ghost/orchestration", ct);
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
    /// <see cref="IUnitOrchestrationStore"/> for an NSubstitute double so
    /// these tests can assert persistence + cache-invalidation behaviour
    /// without standing up a live SpringDbContext + caching decorator.
    /// </summary>
    public sealed class OrchestrationEndpointsFactory : CustomWebApplicationFactory
    {
        public IUnitOrchestrationStore OrchestrationStore { get; }
            = Substitute.For<IUnitOrchestrationStore>();

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                var existing = services
                    .Where(d => d.ServiceType == typeof(IUnitOrchestrationStore))
                    .ToList();
                foreach (var descriptor in existing)
                {
                    services.Remove(descriptor);
                }
                services.AddSingleton(OrchestrationStore);
            });
        }
    }
}