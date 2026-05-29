// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Connector.Slack.Auth.OAuth;
using Cvoya.Spring.Connector.Slack.Configuration;
using Cvoya.Spring.Connector.Slack.Install;
using Cvoya.Spring.Connector.Slack.Provisioning;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Covers <see cref="SlackManifestInstallService"/> — the server-side
/// equivalent of <c>spring connector slack install --write-tenant-secrets</c>
/// that backs the one-page portal wizard (#2882):
/// <list type="bullet">
///   <item><description>Dry-run returns the manifest with no network / persistence / authorize call.</description></item>
///   <item><description>Happy path validates + creates against Slack, persists the canonical tenant secrets, and returns the state-bearing authorize URL.</description></item>
///   <item><description>A Slack validation failure surfaces the structured error code and persists nothing.</description></item>
///   <item><description>A secret-write failure rolls back every secret written this call (atomic — issue #2839).</description></item>
/// </list>
/// </summary>
public sealed class SlackManifestInstallServiceTests
{
    private static readonly Guid TenantId = Guid.Parse("dd55c4ea-8d72-5e43-a9df-88d07af02b69");

    private const string ValidateOk = """{"ok":true}""";
    private const string CreateOk = """
        {
          "ok": true,
          "app_id": "A0123456789",
          "credentials": {
            "client_id": "1234.5678",
            "client_secret": "client-secret-body",
            "signing_secret": "signing-secret-body",
            "verification_token": "verification-token-body"
          }
        }
        """;

    private readonly IHttpClientFactory _httpClientFactory = Substitute.For<IHttpClientFactory>();
    private readonly ISlackOAuthService _oauthService = Substitute.For<ISlackOAuthService>();
    private readonly ISecretStore _secretStore = Substitute.For<ISecretStore>();
    private readonly ISecretRegistry _secretRegistry = Substitute.For<ISecretRegistry>();
    private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();

    public SlackManifestInstallServiceTests()
    {
        _tenantContext.CurrentTenantId.Returns(TenantId);
        _secretStore.WriteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult($"store-key-for::{ci.ArgAt<string>(0)}"));
        _oauthService.BeginAuthorizationAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new SlackAuthorizeResult(
                "https://slack.com/oauth/v2/authorize?client_id=1234.5678&state=sv-state-1",
                "sv-state-1"));
    }

    [Fact]
    public async Task InstallAsync_DryRun_ReturnsManifest_NoNetwork_NoPersist_NoAuthorize()
    {
        var handler = new StubHttpMessageHandler();
        var service = CreateService(handler);

        var result = await service.InstallAsync(
            new SlackManifestInstallRequest(
                ConfigToken: null,
                AppName: "Spring Voyage (dry)",
                SvHost: "https://sv.example.com",
                SocketMode: false,
                DryRun: true,
                ClientState: null),
            TestContext.Current.CancellationToken);

        result.DryRun.ShouldBeTrue();
        result.AppId.ShouldBeNull();
        result.AuthorizeUrl.ShouldBeNull();
        result.WrittenSecretNames.ShouldBeEmpty();
        result.ManifestJson.ShouldContain("\"name\":\"Spring Voyage (dry)\"");
        result.ManifestJson.ShouldContain("/api/v1/tenant/connectors/slack/oauth/callback");

        handler.Requests.ShouldBeEmpty();
        await _secretStore.DidNotReceive().WriteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _oauthService.DidNotReceive().BeginAuthorizationAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_HappyPath_PersistsCanonicalTenantSecrets_AndReturnsAuthorizeUrl()
    {
        var handler = new StubHttpMessageHandler();
        var service = CreateService(handler);

        var result = await service.InstallAsync(
            new SlackManifestInstallRequest(
                ConfigToken: "xoxe.xoxp-test",
                AppName: "Spring Voyage",
                SvHost: "https://sv.example.com",
                SocketMode: false,
                DryRun: false,
                ClientState: """{"targetOrigin":"https://portal.example"}"""),
            TestContext.Current.CancellationToken);

        // Slack was hit in order: validate then create.
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].ShouldEndWith("/api/apps.manifest.validate");
        handler.Requests[1].ShouldEndWith("/api/apps.manifest.create");

        // Every credential persisted as a tenant-scoped, platform-owned
        // secret under the canonical name set shared with the CLI.
        result.WrittenSecretNames.ShouldBe(new[]
        {
            SlackSecretNames.AppId,
            SlackSecretNames.ClientId,
            SlackSecretNames.ClientSecret,
            SlackSecretNames.SigningSecret,
            SlackSecretNames.VerificationToken,
            SlackSecretNames.RedirectUri,
        });
        await _secretStore.Received(6).WriteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _secretRegistry.Received(6).RegisterAsync(
            Arg.Is<SecretRef>(r => r.Scope == SecretScope.Tenant && r.OwnerId == TenantId),
            Arg.Any<string>(),
            SecretOrigin.PlatformOwned,
            Arg.Any<CancellationToken>());

        // The four resolver-consumed names are present (parity guard).
        result.WrittenSecretNames.ShouldContain(SlackOAuthOptionsResolver.SecretNames.ClientId);
        result.WrittenSecretNames.ShouldContain(SlackOAuthOptionsResolver.SecretNames.SigningSecret);
        result.WrittenSecretNames.ShouldContain(SlackOAuthOptionsResolver.SecretNames.RedirectUri);

        result.AppId.ShouldBe("A0123456789");
        result.AuthorizeUrl.ShouldBe(
            "https://slack.com/oauth/v2/authorize?client_id=1234.5678&state=sv-state-1");
        result.State.ShouldBe("sv-state-1");

        // The portal's targetOrigin is forwarded into the OAuth state.
        await _oauthService.Received(1).BeginAuthorizationAsync(
            """{"targetOrigin":"https://portal.example"}""",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_RedirectUriSecret_MatchesManifestCallbackPath()
    {
        var handler = new StubHttpMessageHandler();
        var service = CreateService(handler);

        await service.InstallAsync(
            new SlackManifestInstallRequest(
                ConfigToken: "xoxe.xoxp-test",
                AppName: "Spring Voyage",
                SvHost: "https://sv.example.com/",
                SocketMode: false,
                DryRun: false,
                ClientState: null),
            TestContext.Current.CancellationToken);

        // The redirect-uri secret value is the host (trailing slash
        // trimmed) + the canonical callback path.
        await _secretStore.Received(1).WriteAsync(
            "https://sv.example.com/api/v1/tenant/connectors/slack/oauth/callback",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_SlackValidateFails_ThrowsWithErrorCode_PersistsNothing()
    {
        var handler = new StubHttpMessageHandler
        {
            ValidateResponse = (HttpStatusCode.OK, """{"ok":false,"error":"invalid_auth"}"""),
        };
        var service = CreateService(handler);

        var ex = await Should.ThrowAsync<SlackManifestException>(async () =>
            await service.InstallAsync(
                new SlackManifestInstallRequest(
                    ConfigToken: "xoxe.expired",
                    AppName: "Spring Voyage",
                    SvHost: "https://sv.example.com",
                    SocketMode: false,
                    DryRun: false,
                    ClientState: null),
                TestContext.Current.CancellationToken));

        ex.ErrorCode.ShouldBe("invalid_auth");
        // Never reached create, never persisted, never authorized.
        handler.Requests.Count.ShouldBe(1);
        await _secretStore.DidNotReceive().WriteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _oauthService.DidNotReceive().BeginAuthorizationAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InstallAsync_SecretWriteFails_RollsBackEverySecretWrittenThisCall()
    {
        var handler = new StubHttpMessageHandler();
        var service = CreateService(handler);

        // Fail the 4th registry write; the 3 prior secrets + the in-flight
        // 4th store blob must all be cleaned up.
        var registerCalls = 0;
        _secretRegistry
            .RegisterAsync(
                Arg.Any<SecretRef>(),
                Arg.Any<string>(),
                Arg.Any<SecretOrigin>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                registerCalls++;
                return registerCalls >= 4
                    ? throw new InvalidOperationException("simulated registry failure")
                    : Task.CompletedTask;
            });

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await service.InstallAsync(
                new SlackManifestInstallRequest(
                    ConfigToken: "xoxe.xoxp-test",
                    AppName: "Spring Voyage",
                    SvHost: "https://sv.example.com",
                    SocketMode: false,
                    DryRun: false,
                    ClientState: null),
                TestContext.Current.CancellationToken));

        // 4 store writes attempted (3 committed + the in-flight 4th).
        await _secretStore.Received(4).WriteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Rollback deletes: 3 committed store slots + the orphaned 4th = 4.
        await _secretStore.Received(4).DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Only the 3 committed registry rows are deleted (the 4th never
        // registered).
        await _secretRegistry.Received(3).DeleteAsync(Arg.Any<SecretRef>(), Arg.Any<CancellationToken>());
        // A half-failed install never mints a consent URL.
        await _oauthService.DidNotReceive().BeginAuthorizationAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private SlackManifestInstallService CreateService(StubHttpMessageHandler handler)
    {
        _httpClientFactory.CreateClient(SlackManifestInstallService.HttpClientName)
            .Returns(_ => new HttpClient(handler));
        return new SlackManifestInstallService(
            _httpClientFactory,
            _oauthService,
            _secretStore,
            _secretRegistry,
            _tenantContext,
            NullLoggerFactory.Instance);
    }

    /// <summary>
    /// In-process stand-in for Slack's Manifest API. Routes by request
    /// path; records every path seen so tests can assert the validate →
    /// create order without a real socket.
    /// </summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        public (HttpStatusCode Status, string Body) ValidateResponse { get; set; } =
            (HttpStatusCode.OK, ValidateOk);

        public (HttpStatusCode Status, string Body) CreateResponse { get; set; } =
            (HttpStatusCode.OK, CreateOk);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);

            var (status, body) = path.EndsWith("/api/apps.manifest.create", StringComparison.Ordinal)
                ? CreateResponse
                : ValidateResponse;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }
}
