// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Services;

using System.Security.Claims;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Host.Api.Services;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Service-level tests for <see cref="UnitCreationService"/>. Exercises the
/// creator-identity resolution path introduced for #324 without needing the
/// full HTTP pipeline.
/// </summary>
public class UnitCreationServiceTests
{
    [Fact]
    public async Task CreateAsync_NoHttpContext_FallsBackToApiIdentity()
    {
        // When the service runs outside a request — no ambient HttpContext —
        // the creator identity falls back to the synthetic "api" user so the
        // Owner grant still lands on a deterministic, well-known id rather
        // than an empty string or null. Matches the existing fallback used
        // by MessageEndpoints / AgentEndpoints.
        var fixture = new Fixture();
        fixture.HttpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var result = await fixture.CreateAsync("no-ctx-unit");

        result.Unit.Name.ShouldBe("no-ctx-unit");

        // The grant went to the fallback id — NOT to any claim.
        await fixture.Proxy.Received(1).SetHumanPermissionAsync(
            UnitCreationService.FallbackCreatorId,
            Arg.Is<UnitPermissionEntry>(e =>
                e.HumanId == UnitCreationService.FallbackCreatorId
                && e.Permission == PermissionLevel.Owner),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_AuthenticatedUser_GrantsOwnerToNameIdentifierClaim()
    {
        // When the request arrives with an authenticated principal, the
        // grant must land on that principal's NameIdentifier claim — the
        // same id PermissionHandler consults when evaluating subsequent
        // permission checks. This keeps the round-trip consistent: create
        // → Owner grant → subsequent call passes the router's Viewer gate.
        var fixture = new Fixture();
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "alice@example.com") },
            authenticationType: "test");
        var principal = new ClaimsPrincipal(identity);
        var httpContext = new DefaultHttpContext { User = principal };
        fixture.HttpContextAccessor.HttpContext.Returns(httpContext);

        await fixture.CreateAsync("auth-unit");

        await fixture.Proxy.Received(1).SetHumanPermissionAsync(
            "alice@example.com",
            Arg.Is<UnitPermissionEntry>(e =>
                e.HumanId == "alice@example.com"
                && e.Permission == PermissionLevel.Owner),
            Arg.Any<CancellationToken>());
        await fixture.Proxy.DidNotReceive().SetHumanPermissionAsync(
            UnitCreationService.FallbackCreatorId,
            Arg.Any<UnitPermissionEntry>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_UnauthenticatedPrincipal_FallsBackToApi()
    {
        // An HttpContext with an anonymous ClaimsPrincipal (Identity.IsAuthenticated == false)
        // must NOT be treated as a real caller — fall back to "api" the same
        // way we do when no HttpContext is present at all.
        var fixture = new Fixture();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity()),
        };
        fixture.HttpContextAccessor.HttpContext.Returns(httpContext);

        await fixture.CreateAsync("anon-unit");

        await fixture.Proxy.Received(1).SetHumanPermissionAsync(
            UnitCreationService.FallbackCreatorId,
            Arg.Any<UnitPermissionEntry>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Minimal fixture wiring the <see cref="UnitCreationService"/> up with
    /// stubs for every collaborator so a single test can focus on one
    /// behaviour at a time. <see cref="HttpContextAccessor"/> is exposed so
    /// each test arranges the ambient context as needed.
    /// </summary>
    private sealed class Fixture
    {
        public IDirectoryService Directory { get; } = Substitute.For<IDirectoryService>();
        public IActorProxyFactory ActorProxyFactory { get; } = Substitute.For<IActorProxyFactory>();
        public IHttpContextAccessor HttpContextAccessor { get; } = Substitute.For<IHttpContextAccessor>();
        public IUnitConnectorConfigStore ConnectorConfigStore { get; } = Substitute.For<IUnitConnectorConfigStore>();
        public ISkillBundleResolver BundleResolver { get; } = Substitute.For<ISkillBundleResolver>();
        public ISkillBundleValidator BundleValidator { get; } = Substitute.For<ISkillBundleValidator>();
        public IUnitSkillBundleStore BundleStore { get; } = Substitute.For<IUnitSkillBundleStore>();
        public IUnitActor Proxy { get; } = Substitute.For<IUnitActor>();
        public UnitCreationService Service { get; }

        public Fixture()
        {
            Directory
                .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            Proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
            ActorProxyFactory
                .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
                .Returns(Proxy);
            // IHumanActor resolution is used for the mirror grant; returning
            // null would throw NRE inside the service's try/catch — tests
            // treat that as a non-fatal path and don't care about the mirror
            // itself, but wiring a stub keeps logs clean.
            ActorProxyFactory
                .CreateActorProxy<IHumanActor>(Arg.Any<ActorId>(), Arg.Any<string>())
                .Returns(Substitute.For<IHumanActor>());

            Service = new UnitCreationService(
                Directory,
                ActorProxyFactory,
                HttpContextAccessor,
                ConnectorConfigStore,
                Array.Empty<IConnectorType>(),
                BundleResolver,
                BundleValidator,
                BundleStore,
                NullLoggerFactory.Instance);
        }

        public Task<UnitCreationResult> CreateAsync(string name)
            => Service.CreateAsync(
                new CreateUnitRequest(
                    Name: name,
                    DisplayName: name,
                    Description: "test",
                    Model: null,
                    Color: null,
                    Connector: null),
                CancellationToken.None);
    }
}