// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Host.Api.Services;

using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Regression test for the scratch-wizard auto-start gap: the
/// <c>PUT /api/v1/tenant/units/{id}/execution</c> endpoint must invoke
/// <see cref="IUnitCreationService.TryAutoStartAsync"/> after persisting
/// execution defaults so that units created via the "from scratch" wizard
/// path (which saves image/runtime/model in a follow-up PUT rather than
/// inline with creation) can transition from Draft to Validating without
/// operator intervention.
/// </summary>
public class UnitExecutionEndpointAutoStartTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly Guid UnitActorGuid = new("cccc0001-feed-1234-5678-000000000000");
    private static readonly string UnitActorId = UnitActorGuid.ToString("N");

    private readonly CustomWebApplicationFactory _factory;

    public UnitExecutionEndpointAutoStartTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SetExecution_CallsTryAutoStartAsync_AfterSavingDefaults()
    {
        var ct = TestContext.Current.CancellationToken;

        var mockCreationService = Substitute.For<IUnitCreationService>();
        mockCreationService
            .TryAutoStartAsync(UnitActorGuid, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(LifecycleStatus.Validating);

        var entry = new DirectoryEntry(
            new Address("unit", UnitActorGuid),
            UnitActorGuid,
            "test-unit",
            "Test Unit",
            null,
            DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Id == UnitActorGuid),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        var client = _factory
            .WithWebHostBuilder(b => b.ConfigureTestServices(
                s => s.AddSingleton(mockCreationService)))
            .CreateClient();

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{UnitActorId}/execution",
            new { image = "ghcr.io/test/unit:latest", runtime = "claude-code" },
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await mockCreationService.Received(1)
            .TryAutoStartAsync(UnitActorGuid, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
