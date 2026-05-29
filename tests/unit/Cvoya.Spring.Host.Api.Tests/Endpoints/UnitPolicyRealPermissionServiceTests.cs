// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit-authorization tests that run the <b>real</b>
/// <see cref="PermissionService"/> end-to-end through the
/// <c>/api/v1/tenant/units/{id}/policy</c> gate (#2900). The sibling
/// suites (<see cref="UnitPolicyEndpointsAuthorizationTests"/>,
/// <c>UnitHumansEndpointsTests</c>, <c>UnitTeamMembershipEndpointsTests</c>)
/// arrange a <em>stub</em> <see cref="IPermissionService"/> that returns a
/// canned grant, so the deny/allow outcome is supplied by the mock and a
/// production DI swap to a permissive stub would pass them all green. These
/// tests close that gap: the actual
/// <see cref="PermissionService.ResolveEffectivePermissionAsync"/> runs —
/// the implicit-Owner short-circuit for the operator <c>tenant-user://</c>
/// principal (#2768) and the EF-backed grant-table walk for a non-operator
/// <c>human://</c> caller — so a wiring regression (unregistered service,
/// permissive replacement, broken threshold) is caught.
/// </summary>
/// <remarks>
/// The fixture <see cref="RealPermissionServiceWebApplicationFactory"/>
/// swaps the base fixture's stub for the real service and makes the caller
/// identity controllable per request: in LocalDev the default accessor
/// always resolves the operator TenantUser, which the real service
/// short-circuits to <see cref="PermissionLevel.Owner"/> — so the deny path
/// needs a non-operator <c>human://</c> caller, set via the
/// <see cref="RealPermissionServiceWebApplicationFactory.CallerHumanHeader"/>
/// request header.
/// </remarks>
public class UnitPolicyRealPermissionServiceTests
    : IClassFixture<RealPermissionServiceWebApplicationFactory>
{
    private readonly RealPermissionServiceWebApplicationFactory _factory;
    private readonly HttpClient _operatorClient;

    public UnitPolicyRealPermissionServiceTests(RealPermissionServiceWebApplicationFactory factory)
    {
        _factory = factory;
        // No caller-override header → the accessor resolves the OSS operator
        // TenantUser, the same principal LocalDev surfaces in production.
        _operatorClient = factory.CreateClient();
    }

    [Fact]
    public async Task SetPolicy_OperatorCaller_RealService_ResolvesImplicitOwner_Returns200()
    {
        // Owner-allow through the REAL service: the operator tenant-user://
        // principal hits the #2768 implicit-Owner short-circuit, so the
        // owner-gated PUT succeeds. Falsifiable: if the real service were
        // unwired or denied the operator, this would 403.
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);

        var response = await _operatorClient.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/policy",
            new UnitPolicyResponse(new SkillPolicy(Blocked: new[] { "delete_repo" })),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SetPolicy_NonOperatorCaller_NoGrant_RealService_Returns403()
    {
        // Deny through the REAL service: a non-operator human:// caller with
        // no grant anywhere walks the EF grant table + the (empty) hierarchy
        // and resolves null → 403. This is the assertion the stubbed suites
        // cannot make honestly — a permissive stub swapped into production
        // would return 200 here and this test would fail.
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        ArrangeResolved(unitId);
        using var client = ClientAsHuman(Guid.NewGuid());

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/policy",
            new UnitPolicyResponse(new SkillPolicy(Blocked: new[] { "delete_repo" })),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SetPolicy_NonOperatorCaller_ViewerGrant_RealService_DeniesWrite_Returns403()
    {
        // Deny through the REAL threshold logic: a non-operator human:// with
        // a genuine Viewer grant on the unit resolves Viewer, which is below
        // the Owner bar the PUT requires → 403. The Viewer downgrade is read
        // from the real EF grant row, not supplied by a mock — the
        // single-operator fixture could never produce it.
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        var humanId = Guid.NewGuid();
        ArrangeResolved(unitId);
        await SeedGrantAsync(unitId, humanId, PermissionLevel.Viewer);
        using var client = ClientAsHuman(humanId);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/tenant/units/{unitId:N}/policy",
            new UnitPolicyResponse(new SkillPolicy(Blocked: new[] { "delete_repo" })),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetPolicy_NonOperatorCaller_ViewerGrant_RealService_AllowsRead_Returns200()
    {
        // Allow through the REAL threshold logic: the SAME Viewer grant that
        // is insufficient for the owner-gated PUT above clears the Viewer bar
        // the GET requires → 200. Pairing this with the PUT-deny proves the
        // real ResolveEffectivePermissionAsync threshold comparison runs —
        // a stub returning one canned level could not satisfy both.
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        var humanId = Guid.NewGuid();
        ArrangeResolved(unitId);
        await SeedGrantAsync(unitId, humanId, PermissionLevel.Viewer);
        using var client = ClientAsHuman(humanId);

        var response = await client.GetAsync(
            $"/api/v1/tenant/units/{unitId:N}/policy", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Stubs the Dapr-backed directory so the unit resolves to a
    /// <see cref="DirectoryEntry"/> whose <c>ActorId</c> is the route id —
    /// the same Guid the permission walk and the grant rows key on.
    /// </summary>
    private void ArrangeResolved(Guid unitId)
    {
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == Address.UnitScheme && a.Id == unitId),
                Arg.Any<CancellationToken>())
            .Returns(_ => new DirectoryEntry(
                new Address(Address.UnitScheme, unitId),
                unitId,
                "Test unit",
                "Test unit",
                null,
                DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Plants a direct grant in the real EF-backed
    /// <c>unit_human_permissions</c> table via the production
    /// <see cref="IUnitHumanPermissionStore"/> — the very store the real
    /// <see cref="PermissionService"/> reads back during the walk.
    /// </summary>
    private async Task SeedGrantAsync(Guid unitId, Guid humanId, PermissionLevel level)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = _factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IUnitHumanPermissionStore>();
        await store.UpsertAsync(
            unitId, humanId, new UnitPermissionEntry(humanId.ToString("D"), level), ct);
    }

    /// <summary>
    /// A client whose requests carry the caller-override header, so the
    /// fixture's accessor resolves a non-operator <c>human://</c> identity
    /// the real <see cref="PermissionService"/> evaluates against the grant
    /// table rather than the operator implicit-Owner short-circuit.
    /// </summary>
    private HttpClient ClientAsHuman(Guid humanId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            RealPermissionServiceWebApplicationFactory.CallerHumanHeader, humanId.ToString("D"));
        return client;
    }
}

/// <summary>
/// <see cref="CustomWebApplicationFactory"/> variant that wires the <b>real</b>
/// <see cref="PermissionService"/> (replacing the base fixture's stub) and a
/// header-driven <see cref="IAuthenticatedCallerAccessor"/> so tests can
/// exercise the actual permission resolution for both operator and
/// non-operator callers (#2900).
/// </summary>
public sealed class RealPermissionServiceWebApplicationFactory : CustomWebApplicationFactory
{
    /// <summary>
    /// Request header carrying a Guid; when present the fixture's accessor
    /// resolves the caller to <c>human://&lt;guid&gt;</c> instead of the
    /// default operator <c>tenant-user://</c> principal. Lets a single
    /// shared fixture serve both the Owner-allow and non-operator-deny
    /// paths without per-test mutable state.
    /// </summary>
    public const string CallerHumanHeader = "X-Test-Caller-Human";

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            // Replace the base fixture's stub IPermissionService with the
            // real EF-backed service. PermissionService's collaborators
            // (IUnitHumanPermissionStore, IUnitHierarchyResolver,
            // IUnitLiveConfigStore) are scope-per-call singletons the base
            // fixture leaves intact, so they resolve cleanly against the
            // in-memory DbContext.
            var permDescriptors = services
                .Where(d => d.ServiceType == typeof(IPermissionService))
                .ToList();
            foreach (var descriptor in permDescriptors)
            {
                services.Remove(descriptor);
            }
            services.AddSingleton<IPermissionService, PermissionService>();

            // Replace the production caller accessor (which always resolves
            // the operator TenantUser in LocalDev) with a header-driven one
            // so a non-operator human:// caller can drive the deny path.
            var callerDescriptors = services
                .Where(d => d.ServiceType == typeof(IAuthenticatedCallerAccessor))
                .ToList();
            foreach (var descriptor in callerDescriptors)
            {
                services.Remove(descriptor);
            }
            services.AddScoped<IAuthenticatedCallerAccessor, HeaderDrivenCallerAccessor>();
        });
    }

    /// <summary>
    /// Resolves the caller from the <see cref="CallerHumanHeader"/> request
    /// header: a parseable Guid yields a <c>human://</c> caller; otherwise
    /// the OSS operator <c>tenant-user://</c> principal (the LocalDev
    /// default). Stateless, so it is parallel-safe across tests sharing the
    /// fixture.
    /// </summary>
    private sealed class HeaderDrivenCallerAccessor(IHttpContextAccessor httpContextAccessor)
        : IAuthenticatedCallerAccessor
    {
        public Task<Address?> GetCallerAddressAsync(CancellationToken cancellationToken = default)
        {
            var context = httpContextAccessor.HttpContext;
            if (context is not null
                && context.Request.Headers.TryGetValue(CallerHumanHeader, out var raw)
                && Guid.TryParse(raw.ToString(), out var humanId))
            {
                return Task.FromResult<Address?>(
                    Address.ForIdentity(Address.HumanScheme, humanId));
            }

            return Task.FromResult<Address?>(
                Address.ForIdentity(Address.TenantUserScheme, OssTenantUserIds.Operator));
        }

        public string GetUsername() => AuthConstants.DefaultLocalUserId;
    }
}
