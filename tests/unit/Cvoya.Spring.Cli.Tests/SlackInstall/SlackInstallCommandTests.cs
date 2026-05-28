// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.SlackInstall;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Cli.SlackInstall;

using Shouldly;

using Xunit;

[Collection(ConsoleRedirectionCollection.Name)]
public class SlackInstallCommandTests
{
    [Fact]
    public async Task RunAsync_DryRun_BuildsManifestAndPrintsJson_NoNetwork()
    {
        var stdout = new StringWriter();
        await SlackInstallCommand.RunAsync(
            configToken: null,
            appName: "Spring Voyage (dry)",
            svHostOverride: "https://sv.example.com",
            writeEnv: true,
            writeSecrets: false,
            writeTenantSecrets: false,
            envFilePathOverride: null,
            socketMode: false,
            dryRun: true,
            cancellationToken: CancellationToken.None,
            stdout: stdout);

        var output = stdout.ToString();
        output.ShouldContain("--dry-run");
        output.ShouldContain("Manifest JSON:");
        output.ShouldContain("\"name\":\"Spring Voyage (dry)\"");
        output.ShouldContain("/api/v1/tenant/connectors/slack/oauth/callback");
    }

    [Fact]
    public async Task RunAsync_RejectsBothWriteModes()
    {
        await Should.ThrowAsync<SlackInstallException>(async () =>
        {
            await SlackInstallCommand.RunAsync(
                configToken: "xoxe.test",
                appName: "x",
                svHostOverride: "https://sv.example.com",
                writeEnv: true,
                writeSecrets: true,
                writeTenantSecrets: false,
                envFilePathOverride: null,
                socketMode: false,
                dryRun: false,
                cancellationToken: CancellationToken.None);
        });
    }

    [Fact]
    public async Task RunAsync_RejectsEnvAndTenantSecretsCombination()
    {
        await Should.ThrowAsync<SlackInstallException>(async () =>
        {
            await SlackInstallCommand.RunAsync(
                configToken: "xoxe.test",
                appName: "x",
                svHostOverride: "https://sv.example.com",
                writeEnv: true,
                writeSecrets: false,
                writeTenantSecrets: true,
                envFilePathOverride: null,
                socketMode: false,
                dryRun: false,
                cancellationToken: CancellationToken.None);
        });
    }

    [Fact]
    public async Task RunAsync_RejectsAllThreeWriteModes()
    {
        await Should.ThrowAsync<SlackInstallException>(async () =>
        {
            await SlackInstallCommand.RunAsync(
                configToken: "xoxe.test",
                appName: "x",
                svHostOverride: "https://sv.example.com",
                writeEnv: true,
                writeSecrets: true,
                writeTenantSecrets: true,
                envFilePathOverride: null,
                socketMode: false,
                dryRun: false,
                cancellationToken: CancellationToken.None);
        });
    }

    [Fact]
    public async Task RunAsync_MissingConfigToken_Raises_WhenNotDryRun()
    {
        // Ensure the env var leaks aren't masking the missing flag —
        // the action explicitly clears it for the duration of the test.
        var original = Environment.GetEnvironmentVariable(SlackInstallCommand.ConfigTokenEnvVar);
        Environment.SetEnvironmentVariable(SlackInstallCommand.ConfigTokenEnvVar, null);
        try
        {
            await Should.ThrowAsync<SlackInstallException>(async () =>
            {
                await SlackInstallCommand.RunAsync(
                    configToken: null,
                    appName: "x",
                    svHostOverride: "https://sv.example.com",
                    writeEnv: true,
                    writeSecrets: false,
                    writeTenantSecrets: false,
                    envFilePathOverride: null,
                    socketMode: false,
                    dryRun: false,
                    cancellationToken: CancellationToken.None);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(SlackInstallCommand.ConfigTokenEnvVar, original);
        }
    }

    [Fact]
    public async Task RunAsync_HappyPath_WritesEnvFile()
    {
        using var mockSlack = await MockSlackServer.StartAsync(new Dictionary<string, MockSlackServer.RouteHandler>
        {
            ["/api/apps.manifest.validate"] = _ => (HttpStatusCode.OK, """{"ok":true}"""),
            ["/api/apps.manifest.create"] = _ => (HttpStatusCode.OK, """
                {
                  "ok": true,
                  "app_id": "A0123",
                  "credentials": {
                    "client_id": "1234.5678",
                    "client_secret": "client-secret-body",
                    "signing_secret": "signing-secret-body",
                    "verification_token": "verification-token-body"
                  }
                }
                """),
        });

        var envDir = Path.Combine(Path.GetTempPath(), $"spring-slack-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(envDir);
        var envPath = Path.Combine(envDir, "spring.env");

        using var http = new HttpClient();
        var stdout = new StringWriter();
        try
        {
            await SlackInstallCommand.RunAsync(
                configToken: "xoxe.test-token",
                appName: "Spring Voyage (test)",
                svHostOverride: "https://sv.example.com",
                writeEnv: true,
                writeSecrets: false,
                writeTenantSecrets: false,
                envFilePathOverride: envPath,
                socketMode: false,
                dryRun: false,
                cancellationToken: CancellationToken.None,
                httpClientOverride: http,
                slackBaseUrlOverride: mockSlack.BaseUrl,
                stdout: stdout);

            // Both Slack endpoints hit, in order.
            mockSlack.Received.Count.ShouldBe(2);
            mockSlack.Received[0].Path.ShouldBe("/api/apps.manifest.validate");
            mockSlack.Received[1].Path.ShouldBe("/api/apps.manifest.create");

            // Env file got every credential.
            File.Exists(envPath).ShouldBeTrue();
            var contents = await File.ReadAllTextAsync(envPath, TestContext.Current.CancellationToken);
            contents.ShouldContain("Slack__AppId=A0123");
            contents.ShouldContain("Slack__OAuth__ClientId=1234.5678");
            contents.ShouldContain("Slack__OAuth__ClientSecret=client-secret-body");
            contents.ShouldContain("Slack__OAuth__SigningSecret=signing-secret-body");
            contents.ShouldContain(
                "Slack__OAuth__RedirectUri=https://sv.example.com/api/v1/tenant/connectors/slack/oauth/callback");

            // Env mode can't pre-mint a state-bearing URL because the
            // running service hasn't reloaded Slack:OAuth:* yet. Direct
            // operators at the portal entry point instead.
            var output = stdout.ToString();
            output.ShouldContain("https://sv.example.com/connectors/slack");
            output.ShouldNotContain("slack.com/oauth/v2/authorize");
        }
        finally
        {
            Directory.Delete(envDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_HappyPath_WriteTenantSecrets_PreMintsStateBearingUrl()
    {
        // Issue: Slack's manifest.create.oauth_authorize_url has no
        // `state` param, so the SV callback rejects it. For tenant /
        // platform persistence the secret registry resolves per-call,
        // so the CLI can call SV's /oauth/authorize right away to get
        // a state-bearing consent URL. This test exercises that path.
        using var mockSlack = await MockSlackServer.StartAsync(new Dictionary<string, MockSlackServer.RouteHandler>
        {
            ["/api/apps.manifest.validate"] = _ => (HttpStatusCode.OK, """{"ok":true}"""),
            ["/api/apps.manifest.create"] = _ => (HttpStatusCode.OK, """
                {
                  "ok": true,
                  "app_id": "A0123",
                  "credentials": {
                    "client_id": "1234.5678",
                    "client_secret": "client-secret-body",
                    "signing_secret": "signing-secret-body",
                    "verification_token": "verification-token-body"
                  },
                  "oauth_authorize_url": "https://slack.com/oauth/v2/authorize?client_id=1234.5678"
                }
                """),
        });

        var routes = new List<MockSpringApiServer.RouteRule>
        {
            new(
                "POST",
                "/api/v1/tenant/secrets",
                MockSpringApiServer.RouteMatch.Exact,
                (method, path, body) =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    var name = doc.RootElement.GetProperty("name").GetString()!;
                    return (HttpStatusCode.OK, $$"""{"name":"{{name}}","version":1}""");
                }),
            new(
                "POST",
                "/api/v1/tenant/connectors/slack/oauth/authorize",
                MockSpringApiServer.RouteMatch.Exact,
                (method, path, body) => (HttpStatusCode.OK, """
                    {
                      "authorizeUrl": "https://slack.com/oauth/v2/authorize?client_id=1234.5678&state=svstate1",
                      "state": "svstate1"
                    }
                    """)),
        };
        using var mockApi = await MockSpringApiServer.StartAsync(routes);

        using var http = new HttpClient();
        SpringApiClient ApiClientFactory() => new(new HttpClient(), mockApi.BaseUrl);

        var stdout = new StringWriter();
        await SlackInstallCommand.RunAsync(
            configToken: "xoxe.test-token",
            appName: "Spring Voyage (test)",
            svHostOverride: "https://sv.example.com",
            writeEnv: false,
            writeSecrets: false,
            writeTenantSecrets: true,
            envFilePathOverride: null,
            socketMode: false,
            dryRun: false,
            cancellationToken: CancellationToken.None,
            httpClientOverride: http,
            slackBaseUrlOverride: mockSlack.BaseUrl,
            apiClientFactoryOverride: ApiClientFactory,
            stdout: stdout);

        // Tenant-secret writes (6 keys) + the authorize call = 7 POSTs.
        var authorizeCalls = 0;
        foreach (var req in mockApi.Received)
        {
            if (req.Path == "/api/v1/tenant/connectors/slack/oauth/authorize" && req.Method == "POST")
            {
                authorizeCalls++;
            }
        }
        authorizeCalls.ShouldBe(1);

        // The printed URL is SV's state-bearing one, NOT Slack's
        // stateless manifest.create.oauth_authorize_url.
        var output = stdout.ToString();
        output.ShouldContain("state=svstate1");
        output.ShouldNotContain("client_id=1234.5678&\n"); // sanity: not the bare manifest URL
    }

    [Fact]
    public async Task RunAsync_HappyPath_WriteTenantSecrets_AuthorizeFailure_FallsBackToPortal()
    {
        // If /oauth/authorize fails (e.g. server unreachable, resolver
        // cache lag, mis-config), the credentials have already been
        // written so we don't roll back. The success printer falls
        // back to the portal install entry point.
        using var mockSlack = await MockSlackServer.StartAsync(new Dictionary<string, MockSlackServer.RouteHandler>
        {
            ["/api/apps.manifest.validate"] = _ => (HttpStatusCode.OK, """{"ok":true}"""),
            ["/api/apps.manifest.create"] = _ => (HttpStatusCode.OK, """
                {
                  "ok": true,
                  "app_id": "A0123",
                  "credentials": {
                    "client_id": "1234.5678",
                    "client_secret": "client-secret-body",
                    "signing_secret": "signing-secret-body",
                    "verification_token": "verification-token-body"
                  }
                }
                """),
        });

        var routes = new List<MockSpringApiServer.RouteRule>
        {
            new(
                "POST",
                "/api/v1/tenant/secrets",
                MockSpringApiServer.RouteMatch.Exact,
                (method, path, body) =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    var name = doc.RootElement.GetProperty("name").GetString()!;
                    return (HttpStatusCode.OK, $$"""{"name":"{{name}}","version":1}""");
                }),
            new(
                "POST",
                "/api/v1/tenant/connectors/slack/oauth/authorize",
                MockSpringApiServer.RouteMatch.Exact,
                (method, path, body) => (HttpStatusCode.BadGateway,
                    """{"title":"Slack OAuth is not configured","status":502}""")),
        };
        using var mockApi = await MockSpringApiServer.StartAsync(routes);

        using var http = new HttpClient();
        SpringApiClient ApiClientFactory() => new(new HttpClient(), mockApi.BaseUrl);

        var stdout = new StringWriter();
        await SlackInstallCommand.RunAsync(
            configToken: "xoxe.test-token",
            appName: "Spring Voyage (test)",
            svHostOverride: "https://sv.example.com",
            writeEnv: false,
            writeSecrets: false,
            writeTenantSecrets: true,
            envFilePathOverride: null,
            socketMode: false,
            dryRun: false,
            cancellationToken: CancellationToken.None,
            httpClientOverride: http,
            slackBaseUrlOverride: mockSlack.BaseUrl,
            apiClientFactoryOverride: ApiClientFactory,
            stdout: stdout);

        var output = stdout.ToString();
        output.ShouldContain("Could not pre-mint a state-bearing install URL");
        output.ShouldContain("https://sv.example.com/connectors/slack");
    }

    [Fact]
    public async Task RunAsync_SlackValidateFails_Throws()
    {
        using var mockSlack = await MockSlackServer.StartAsync(new Dictionary<string, MockSlackServer.RouteHandler>
        {
            ["/api/apps.manifest.validate"] =
                _ => (HttpStatusCode.OK, """{"ok":false,"error":"invalid_manifest"}"""),
        });

        using var http = new HttpClient();
        var ex = await Should.ThrowAsync<SlackManifestException>(async () =>
        {
            await SlackInstallCommand.RunAsync(
                configToken: "xoxe.test-token",
                appName: "x",
                svHostOverride: "https://sv.example.com",
                writeEnv: true,
                writeSecrets: false,
                writeTenantSecrets: false,
                envFilePathOverride: Path.Combine(Path.GetTempPath(), "no-write.env"),
                socketMode: false,
                dryRun: false,
                cancellationToken: CancellationToken.None,
                httpClientOverride: http,
                slackBaseUrlOverride: mockSlack.BaseUrl);
        });
        ex.ErrorCode.ShouldBe("invalid_manifest");
        // We never reached the create call.
        mockSlack.Received.Count.ShouldBe(1);
        mockSlack.Received[0].Path.ShouldBe("/api/apps.manifest.validate");
    }

    [Fact]
    public async Task RunAsync_WriteSecrets_RollsBackOnFailure()
    {
        // Issue #2839 acceptance: on any secret-write failure, every
        // secret already written in this run is rolled back so the
        // platform-secret store never ends up half-populated.
        using var mockSlack = await MockSlackServer.StartAsync(new Dictionary<string, MockSlackServer.RouteHandler>
        {
            ["/api/apps.manifest.validate"] = _ => (HttpStatusCode.OK, """{"ok":true}"""),
            ["/api/apps.manifest.create"] = _ => (HttpStatusCode.OK, """
                {
                  "ok": true,
                  "app_id": "A0123",
                  "credentials": {
                    "client_id": "1234.5678",
                    "client_secret": "client-secret-body",
                    "signing_secret": "signing-secret-body",
                    "verification_token": "verification-token-body"
                  }
                }
                """),
        });

        // SV mock: the first 3 platform-secret POSTs succeed; the 4th
        // (signing-secret) returns 500. We then expect 3 DELETEs (for
        // the three names already written) to land before the run
        // throws.
        var postCounter = 0;
        var writeSucceededNames = new ConcurrentBag<string>();
        var routes = new List<MockSpringApiServer.RouteRule>
        {
            new(
                "POST",
                "/api/v1/platform/secrets",
                MockSpringApiServer.RouteMatch.Exact,
                (method, path, body) =>
                {
                    var count = Interlocked.Increment(ref postCounter);
                    if (count >= 4)
                    {
                        return (HttpStatusCode.InternalServerError,
                            """{"error":"simulated server error"}""");
                    }
                    // Extract the name from the JSON body so the rollback
                    // assertion can match each DELETE to a known prior POST.
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    var name = doc.RootElement.GetProperty("name").GetString()!;
                    writeSucceededNames.Add(name);
                    return (HttpStatusCode.OK,
                        $$"""{"name":"{{name}}","version":1}""");
                }),
            new(
                "DELETE",
                "/api/v1/platform/secrets/",
                MockSpringApiServer.RouteMatch.Prefix,
                (method, path, body) => (HttpStatusCode.NoContent, "")),
        };
        using var mockApi = await MockSpringApiServer.StartAsync(routes);

        using var http = new HttpClient();
        // Build an SV API client that points at the in-process mock.
        SpringApiClient ApiClientFactory() => new(new HttpClient(), mockApi.BaseUrl);

        await Should.ThrowAsync<Exception>(async () =>
        {
            await SlackInstallCommand.RunAsync(
                configToken: "xoxe.test-token",
                appName: "x",
                svHostOverride: "https://sv.example.com",
                writeEnv: false,
                writeSecrets: true,
                writeTenantSecrets: false,
                envFilePathOverride: null,
                socketMode: false,
                dryRun: false,
                cancellationToken: CancellationToken.None,
                httpClientOverride: http,
                slackBaseUrlOverride: mockSlack.BaseUrl,
                apiClientFactoryOverride: ApiClientFactory);
        });

        // We expect exactly 4 POSTs (3 succeed, 4th fails) and 3
        // DELETEs (one per successful POST).
        var posts = 0;
        var deletes = 0;
        foreach (var req in mockApi.Received)
        {
            if (string.Equals(req.Method, "POST", StringComparison.Ordinal))
            {
                posts++;
            }
            else if (string.Equals(req.Method, "DELETE", StringComparison.Ordinal))
            {
                deletes++;
                // DELETEs target the same names that previously
                // succeeded — confirms targeted rollback rather than a
                // blind sweep.
                var lastSegment = req.Path.Substring(req.Path.LastIndexOf('/') + 1);
                writeSucceededNames.ShouldContain(lastSegment);
            }
        }

        posts.ShouldBe(4);
        deletes.ShouldBe(3);
    }

    [Fact]
    public async Task RunAsync_WriteTenantSecrets_RollsBackOnFailure()
    {
        // Issue #2849: tenant-secret path mirrors the platform-secret
        // rollback contract. On any tenant-secret-write failure, every
        // tenant secret already written in this run is rolled back.
        using var mockSlack = await MockSlackServer.StartAsync(new Dictionary<string, MockSlackServer.RouteHandler>
        {
            ["/api/apps.manifest.validate"] = _ => (HttpStatusCode.OK, """{"ok":true}"""),
            ["/api/apps.manifest.create"] = _ => (HttpStatusCode.OK, """
                {
                  "ok": true,
                  "app_id": "A0123",
                  "credentials": {
                    "client_id": "1234.5678",
                    "client_secret": "client-secret-body",
                    "signing_secret": "signing-secret-body",
                    "verification_token": "verification-token-body"
                  }
                }
                """),
        });

        var postCounter = 0;
        var writeSucceededNames = new ConcurrentBag<string>();
        var routes = new List<MockSpringApiServer.RouteRule>
        {
            new(
                "POST",
                "/api/v1/tenant/secrets",
                MockSpringApiServer.RouteMatch.Exact,
                (method, path, body) =>
                {
                    var count = Interlocked.Increment(ref postCounter);
                    if (count >= 4)
                    {
                        return (HttpStatusCode.InternalServerError,
                            """{"error":"simulated server error"}""");
                    }
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    var name = doc.RootElement.GetProperty("name").GetString()!;
                    writeSucceededNames.Add(name);
                    return (HttpStatusCode.OK,
                        $$"""{"name":"{{name}}","version":1}""");
                }),
            new(
                "DELETE",
                "/api/v1/tenant/secrets/",
                MockSpringApiServer.RouteMatch.Prefix,
                (method, path, body) => (HttpStatusCode.NoContent, "")),
        };
        using var mockApi = await MockSpringApiServer.StartAsync(routes);

        using var http = new HttpClient();
        SpringApiClient ApiClientFactory() => new(new HttpClient(), mockApi.BaseUrl);

        await Should.ThrowAsync<Exception>(async () =>
        {
            await SlackInstallCommand.RunAsync(
                configToken: "xoxe.test-token",
                appName: "x",
                svHostOverride: "https://sv.example.com",
                writeEnv: false,
                writeSecrets: false,
                writeTenantSecrets: true,
                envFilePathOverride: null,
                socketMode: false,
                dryRun: false,
                cancellationToken: CancellationToken.None,
                httpClientOverride: http,
                slackBaseUrlOverride: mockSlack.BaseUrl,
                apiClientFactoryOverride: ApiClientFactory);
        });

        var posts = 0;
        var deletes = 0;
        foreach (var req in mockApi.Received)
        {
            if (string.Equals(req.Method, "POST", StringComparison.Ordinal))
            {
                posts++;
            }
            else if (string.Equals(req.Method, "DELETE", StringComparison.Ordinal))
            {
                deletes++;
                var lastSegment = req.Path.Substring(req.Path.LastIndexOf('/') + 1);
                writeSucceededNames.ShouldContain(lastSegment);
            }
        }

        posts.ShouldBe(4);
        deletes.ShouldBe(3);
    }
}
