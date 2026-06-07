// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Auth;

using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;

using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Host.Api.Auth;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Regression suite for #3071 (umbrella #2786): proves the OSS "observe
/// every engagement" capability is delivered <em>through the role model</em>
/// — <see cref="PlatformRoles.TenantObserver"/> granted by
/// <see cref="IRoleClaimSource"/> — and never via a path that special-cases
/// the OSS single user.
/// </summary>
/// <remarks>
/// <para>
/// The OSS overlay registers <see cref="OssAllRolesClaimSource"/>, which
/// grants every authenticated caller every role; that overlay's behaviour
/// is pinned unchanged by <see cref="OssAllRolesClaimSourceTests"/>,
/// <see cref="AuthHandlerRoleClaimsTests"/>, and <see cref="RolePoliciesTests"/>.
/// </para>
/// <para>
/// This suite swaps in a <em>cloud-shaped per-identity</em>
/// <see cref="IRoleClaimSource"/> — the exact DI seam the private cloud
/// overlay uses — that scopes the granted role subset by the caller's
/// <see cref="ClaimTypes.NameIdentifier"/>. A companion test auth scheme
/// stamps the identity from an <c>X-Test-Identity</c> request header so a
/// single host can exercise several identities. The assertions then confirm
/// each tenant-wide <em>observation</em> surface gates purely on
/// <see cref="ClaimTypes.Role"/>:
/// </para>
/// <list type="bullet">
///   <item><description>a <c>TenantObserver</c>-only identity observes every
///     engagement (threads + interactions) but cannot send;</description></item>
///   <item><description>a plain <c>TenantUser</c> is denied the observation
///     surfaces yet still reaches the participant-scoped surfaces;</description></item>
///   <item><description>an identity holding no role is denied everywhere.</description></item>
/// </list>
/// <para>
/// <b>Scope.</b> This is the role/observer half of #3071. The participation
/// half — thread-membership-decides-receive and owner-by-creation, which the
/// participant-scoped <c>/threads</c> view, <c>IInboxIdentityResolver</c>,
/// <c>IThreadQueryService</c> participant filter, and the
/// <c>HumanActor</c> receive gate implement — is gated on the #1479
/// authorization-model decision and is deliberately NOT exercised here.
/// These tests therefore assert only the role-policy boundary (which
/// surface a given role can reach), not which rows a participant-scoped
/// query returns.
/// </para>
/// </remarks>
public class CloudShapedRoleScopingTests
{
    /// <summary>Header the test auth handler reads to choose the caller identity.</summary>
    private const string IdentityHeader = "X-Test-Identity";

    /// <summary>Identity that the cloud-shaped source grants only <see cref="PlatformRoles.TenantObserver"/>.</summary>
    private const string ObserverOnlyIdentity = "observer-only";

    /// <summary>Identity that the cloud-shaped source grants only <see cref="PlatformRoles.TenantUser"/>.</summary>
    private const string TenantUserIdentity = "tenant-user-only";

    /// <summary>Identity that the cloud-shaped source grants no role at all.</summary>
    private const string UnprivilegedIdentity = "no-roles";

    private const string ObservationThreads = "/api/v1/tenant/observation/threads";
    private const string ObservationInteractions = "/api/v1/tenant/observation/interactions";
    private const string ParticipantThreads = "/api/v1/tenant/threads";

    // -----------------------------------------------------------------------
    // TenantObserver-only: observes every engagement, cannot send.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ObserverOnly_ObservationThreads_Allowed()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        using var request = Request(HttpMethod.Get, ObservationThreads, ObserverOnlyIdentity);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ObserverOnly_ObservationInteractions_Allowed()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        using var request = Request(HttpMethod.Get, ObservationInteractions, ObserverOnlyIdentity);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ObserverOnly_SendIntoThread_Forbidden()
    {
        // Observation is read-only. Holding TenantObserver — even into a
        // thread the caller happens to participate in — does NOT confer
        // the TenantUser send capability. The send surface
        // (POST /threads/{id}/messages) is gated TenantUser, so an
        // observer-only identity is rejected by the role policy before any
        // participation question is reached.
        using var factory = new Factory();
        using var client = factory.CreateClient();
        using var request = Request(
            HttpMethod.Post,
            $"{ParticipantThreads}/{Guid.NewGuid():N}/messages",
            ObserverOnlyIdentity);
        request.Content = JsonContent("{\"to\":{\"scheme\":\"agent\",\"path\":\"" + Guid.NewGuid().ToString("N") + "\"},\"text\":\"hi\"}");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ObserverOnly_ParticipantThreads_Forbidden()
    {
        // The participant-scoped /threads list is a TenantUser surface.
        // TenantObserver is a strictly read-all observation role; it does
        // not grant the in-product TenantUser surfaces, so the observer-only
        // identity is denied here — observation lives at /observation/*.
        using var factory = new Factory();
        using var client = factory.CreateClient();
        using var request = Request(HttpMethod.Get, ParticipantThreads, ObserverOnlyIdentity);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------------
    // Plain TenantUser: denied observation, reaches participant surfaces.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TenantUserOnly_ObservationThreads_Forbidden()
    {
        // A plain TenantUser cannot observe engagements it is not part of.
        // The observation surface requires TenantObserver; the role policy
        // denies the TenantUser-only identity.
        using var factory = new Factory();
        using var client = factory.CreateClient();
        using var request = Request(HttpMethod.Get, ObservationThreads, TenantUserIdentity);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TenantUserOnly_ObservationInteractions_Forbidden()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        using var request = Request(HttpMethod.Get, ObservationInteractions, TenantUserIdentity);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TenantUserOnly_ParticipantThreads_Allowed()
    {
        // The TenantUser role reaches the participant-scoped /threads
        // surface. (Which rows it returns is the participation half —
        // out of scope here; this asserts only that the role policy admits
        // the TenantUser identity to the surface.)
        using var factory = new Factory();
        using var client = factory.CreateClient();
        using var request = Request(HttpMethod.Get, ParticipantThreads, TenantUserIdentity);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // -----------------------------------------------------------------------
    // Unprivileged: denied everywhere.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(ObservationThreads)]
    [InlineData(ObservationInteractions)]
    [InlineData(ParticipantThreads)]
    public async Task Unprivileged_EverySurface_Forbidden(string path)
    {
        // An authenticated identity that the cloud-shaped source grants no
        // role is denied every role-gated surface — observation and
        // participant alike. Authentication succeeds (so this is 403, not
        // 401); authorization fails because no role claim is present.
        using var factory = new Factory();
        using var client = factory.CreateClient();
        using var request = Request(HttpMethod.Get, path, UnprivilegedIdentity);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string identity)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(IdentityHeader, identity);
        return request;
    }

    private static StringContent JsonContent(string json) =>
        new(json, System.Text.Encoding.UTF8, "application/json");

    /// <summary>
    /// Factory that swaps the OSS auth + role wiring for a cloud-shaped,
    /// per-identity model: a test auth scheme that stamps the identity from
    /// the <c>X-Test-Identity</c> header, and a
    /// <see cref="CloudShapedRoleClaimSource"/> that returns a different role
    /// subset per identity. The observation query services are stubbed to
    /// empty so an authorised request reaches a 200 without a real DB.
    /// </summary>
    private sealed class Factory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                // Cloud-shaped per-identity role source — the DI seam the
                // private overlay uses. Replaces the OSS all-roles default.
                Replace<IRoleClaimSource>(services, new CloudShapedRoleClaimSource());

                // Header-driven test auth handler, registered as the default
                // scheme so the role policies evaluate against the per-identity
                // claims this handler stamps. The OSS LocalDevAuthHandler stays
                // registered but is no longer the default.
                services
                    .AddAuthentication(HeaderIdentityAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, HeaderIdentityAuthHandler>(
                        HeaderIdentityAuthHandler.SchemeName, null);
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultScheme = HeaderIdentityAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = HeaderIdentityAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = HeaderIdentityAuthHandler.SchemeName;
                });

                // Observation reads hit these services; stub them to empty so
                // an authorised request lands on 200 without a Postgres backing.
                var threadQuery = Substitute.For<IThreadQueryService>();
                threadQuery
                    .ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
                    .Returns(Array.Empty<ThreadSummary>());
                Replace(services, threadQuery);

                var interactions = Substitute.For<IInteractionsQueryService>();
                interactions
                    .GetAsync(Arg.Any<InteractionsQueryFilters>(), Arg.Any<CancellationToken>())
                    .Returns(new InteractionsGraph(
                        Array.Empty<InteractionsNode>(),
                        Array.Empty<InteractionsEdge>(),
                        Array.Empty<InteractionsTimelineBucket>(),
                        Truncated: null));
                Replace(services, interactions);
            });
        }

        private static void Replace<TService>(IServiceCollection services, TService instance)
            where TService : class
        {
            var existing = services.Where(d => d.ServiceType == typeof(TService)).ToList();
            foreach (var d in existing)
            {
                services.Remove(d);
            }
            services.AddSingleton(instance);
        }
    }

    /// <summary>
    /// Stand-in for the cloud overlay's <see cref="IRoleClaimSource"/>: the
    /// granted role subset is a function of the caller's
    /// <see cref="ClaimTypes.NameIdentifier"/>. Unknown identities get no
    /// role. This is the only knob the overlay needs to turn the single-user
    /// OSS deployment into a multi-identity one — every downstream decision
    /// reads <see cref="ClaimTypes.Role"/>, so scoping the claim here scopes
    /// every surface.
    /// </summary>
    private sealed class CloudShapedRoleClaimSource : IRoleClaimSource
    {
        public IEnumerable<Claim> GetRoleClaims(ClaimsIdentity identity)
        {
            var subject = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var role = subject switch
            {
                ObserverOnlyIdentity => PlatformRoles.TenantObserver,
                TenantUserIdentity => PlatformRoles.TenantUser,
                _ => null,
            };

            if (role is not null)
            {
                yield return new Claim(ClaimTypes.Role, role);
            }
        }
    }

    /// <summary>
    /// Test auth handler that authenticates every request, stamping the
    /// <see cref="ClaimTypes.NameIdentifier"/> from the
    /// <c>X-Test-Identity</c> header (defaulting to an unprivileged id when
    /// absent) and appending the role claims the registered
    /// <see cref="IRoleClaimSource"/> returns for that identity — mirroring
    /// how <see cref="LocalDevAuthHandler"/> / <see cref="ApiTokenAuthHandler"/>
    /// consult the source. Authentication always succeeds, so a denied
    /// request surfaces as 403 (role policy), not 401.
    /// </summary>
    private sealed class HeaderIdentityAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        IRoleClaimSource roleClaimSource)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
    {
        public const string SchemeName = "TestHeaderIdentity";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var subject = Request.Headers.TryGetValue(IdentityHeader, out var values)
                && !string.IsNullOrWhiteSpace(values.ToString())
                    ? values.ToString()
                    : UnprivilegedIdentity;

            var identity = new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, subject) },
                SchemeName);
            identity.AddClaims(roleClaimSource.GetRoleClaims(identity));

            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
