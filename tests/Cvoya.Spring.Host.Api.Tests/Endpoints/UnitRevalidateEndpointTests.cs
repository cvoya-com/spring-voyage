// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for <c>POST /api/v1/units/{id}/revalidate</c> (#947 /
/// T-05). The endpoint is allowed from <see cref="UnitStatus.Error"/> and
/// <see cref="UnitStatus.Stopped"/> only; every other status rejects with a
/// 409 containing a structured <c>currentStatus</c> detail so the client
/// can surface the mismatch.
/// </summary>
public class UnitRevalidateEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private const string UnitName = "engineering";
    private const string ActorId = "actor-engineering";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitRevalidateEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData(UnitStatus.Error)]
    [InlineData(UnitStatus.Stopped)]
    public async Task Revalidate_FromAllowedStatus_Returns202_TransitionsToValidating(
        UnitStatus from)
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(from);
        proxy.TransitionAsync(UnitStatus.Validating, Arg.Any<CancellationToken>())
            .Returns(new TransitionResult(true, UnitStatus.Validating, null));
        ArrangeResolved(proxy);

        var response = await _client.PostAsync(
            $"/api/v1/tenant/units/{UnitName}/revalidate", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await proxy.Received(1).TransitionAsync(
            UnitStatus.Validating, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(UnitStatus.Draft)]
    [InlineData(UnitStatus.Validating)]
    [InlineData(UnitStatus.Running)]
    [InlineData(UnitStatus.Starting)]
    [InlineData(UnitStatus.Stopping)]
    public async Task Revalidate_FromInvalidStatus_Returns409_WithCurrentStatus(
        UnitStatus from)
    {
        var ct = TestContext.Current.CancellationToken;
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(from);
        ArrangeResolved(proxy);

        var response = await _client.PostAsync(
            $"/api/v1/tenant/units/{UnitName}/revalidate", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await proxy.DidNotReceive().TransitionAsync(
            UnitStatus.Validating, Arg.Any<CancellationToken>());

        // The 409 ProblemDetails must carry a structured payload with the
        // current status so the client can render useful guidance.
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("code").GetString().ShouldBe("InvalidState");
        doc.RootElement.GetProperty("currentStatus").GetString().ShouldBe(from.ToString());
    }

    [Fact]
    public async Task Revalidate_UnknownUnit_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.PostAsync(
            "/api/v1/tenant/units/nope/revalidate", content: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private void ArrangeResolved(IUnitActor proxy)
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();

        var entry = new DirectoryEntry(
            new Address("unit", UnitName),
            ActorId,
            "Engineering",
            "Engineering unit",
            null,
            DateTimeOffset.UtcNow);

        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == UnitName),
                Arg.Any<CancellationToken>())
            .Returns(entry);

        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == ActorId),
                Arg.Any<string>())
            .Returns(proxy);
    }
}