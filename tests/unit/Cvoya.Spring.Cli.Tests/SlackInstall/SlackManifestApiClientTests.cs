// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.SlackInstall;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using Cvoya.Spring.Cli.SlackInstall;

using Shouldly;

using Xunit;

public class SlackManifestApiClientTests
{
    [Fact]
    public async Task ValidateAsync_OkResponse_DoesNotThrow_AndForwardsConfigToken()
    {
        using var mock = await MockSlackServer.StartAsync(new Dictionary<string, MockSlackServer.RouteHandler>
        {
            ["/api/apps.manifest.validate"] = _ => (HttpStatusCode.OK, """{"ok":true}"""),
        });

        using var http = new HttpClient();
        var client = new SlackManifestApiClient(http, mock.BaseUrl);

        await client.ValidateAsync(
            manifestJson: """{"display_information":{"name":"x"}}""",
            configurationToken: "xoxe.xoxp-test-token",
            cancellationToken: TestContext.Current.CancellationToken);

        mock.Received.Count.ShouldBe(1);
        var req = mock.Received[0];
        req.Method.ShouldBe("POST");
        req.Path.ShouldBe("/api/apps.manifest.validate");
        req.Authorization.ShouldBe("Bearer xoxe.xoxp-test-token");
        // The body wraps the manifest under a `manifest` field per Slack's API.
        req.Body.ShouldContain("\"manifest\"");
    }

    [Fact]
    public async Task ValidateAsync_NotOk_RaisesWithSlackError()
    {
        using var mock = await MockSlackServer.StartAsync(new Dictionary<string, MockSlackServer.RouteHandler>
        {
            ["/api/apps.manifest.validate"] =
                _ => (HttpStatusCode.OK, """{"ok":false,"error":"invalid_manifest"}"""),
        });

        using var http = new HttpClient();
        var client = new SlackManifestApiClient(http, mock.BaseUrl);

        var ex = await Should.ThrowAsync<SlackManifestException>(async () =>
            await client.ValidateAsync(
                """{"display_information":{"name":"x"}}""",
                "xoxe.xoxp-test-token",
                TestContext.Current.CancellationToken));
        ex.ErrorCode.ShouldBe("invalid_manifest");
        ex.ResponseBody!.ShouldContain("invalid_manifest");
    }

    [Fact]
    public async Task CreateAsync_HappyPath_ParsesCredentials()
    {
        using var mock = await MockSlackServer.StartAsync(new Dictionary<string, MockSlackServer.RouteHandler>
        {
            ["/api/apps.manifest.create"] = _ => (HttpStatusCode.OK, """
                {
                  "ok": true,
                  "app_id": "A0123456789",
                  "credentials": {
                    "client_id": "1234.5678",
                    "client_secret": "client-secret-body",
                    "signing_secret": "signing-secret-body",
                    "verification_token": "verification-token-body"
                  },
                  "oauth_authorize_url": "https://slack.com/oauth/v2/authorize?client_id=1234.5678&scope=..."
                }
                """),
        });

        using var http = new HttpClient();
        var client = new SlackManifestApiClient(http, mock.BaseUrl);

        var result = await client.CreateAsync(
            manifestJson: """{"display_information":{"name":"x"}}""",
            configurationToken: "xoxe.xoxp-test-token",
            cancellationToken: TestContext.Current.CancellationToken);

        result.AppId.ShouldBe("A0123456789");
        result.Credentials!.ClientId.ShouldBe("1234.5678");
        result.Credentials.ClientSecret.ShouldBe("client-secret-body");
        result.Credentials.SigningSecret.ShouldBe("signing-secret-body");
        result.Credentials.VerificationToken.ShouldBe("verification-token-body");
    }

    [Fact]
    public async Task CreateAsync_NotOk_RaisesWithSlackError()
    {
        using var mock = await MockSlackServer.StartAsync(new Dictionary<string, MockSlackServer.RouteHandler>
        {
            ["/api/apps.manifest.create"] =
                _ => (HttpStatusCode.OK, """{"ok":false,"error":"token_revoked"}"""),
        });

        using var http = new HttpClient();
        var client = new SlackManifestApiClient(http, mock.BaseUrl);

        var ex = await Should.ThrowAsync<SlackManifestException>(async () =>
            await client.CreateAsync(
                """{"display_information":{"name":"x"}}""",
                "xoxe.xoxp-test-token",
                TestContext.Current.CancellationToken));
        ex.ErrorCode.ShouldBe("token_revoked");
    }

    [Fact]
    public async Task CreateAsync_MissingCredentials_Raises()
    {
        // ok=true but Slack omits the credentials block — the client
        // should refuse to return so the operator doesn't write nulls
        // into spring.env / secrets.
        using var mock = await MockSlackServer.StartAsync(new Dictionary<string, MockSlackServer.RouteHandler>
        {
            ["/api/apps.manifest.create"] = _ => (HttpStatusCode.OK, """
                {"ok":true,"app_id":"A0123","credentials":{"client_id":"x","signing_secret":""}}
                """),
        });

        using var http = new HttpClient();
        var client = new SlackManifestApiClient(http, mock.BaseUrl);

        await Should.ThrowAsync<SlackManifestException>(async () =>
            await client.CreateAsync(
                """{"display_information":{"name":"x"}}""",
                "xoxe.xoxp-test-token",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateAsync_RejectsEmptyManifest()
    {
        using var http = new HttpClient();
        var client = new SlackManifestApiClient(http, "http://127.0.0.1:0");
        await Should.ThrowAsync<ArgumentException>(async () =>
            await client.ValidateAsync("", "token", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateAsync_RejectsEmptyToken()
    {
        using var http = new HttpClient();
        var client = new SlackManifestApiClient(http, "http://127.0.0.1:0");
        await Should.ThrowAsync<ArgumentException>(async () =>
            await client.ValidateAsync("""{"a":1}""", "", TestContext.Current.CancellationToken));
    }
}
