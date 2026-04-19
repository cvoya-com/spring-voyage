// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.GitHubApp;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Cli.Commands;
using Cvoya.Spring.Cli.GitHubApp;

using Shouldly;

using Xunit;

public class GitHubAppCommandTests
{
    [Fact]
    public async Task RunAsync_DryRun_BuildsManifestAndPrintsUrl_NoNetwork()
    {
        var stdout = new StringWriter();
        await GitHubAppCommand.RunAsync(
            name: "Spring Voyage (dry)",
            org: null,
            webhookUrlOverride: "https://example.com/api/v1/webhooks/github",
            writeEnv: false,
            writeSecrets: false,
            envFilePathOverride: null,
            dryRun: true,
            callbackTimeout: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None,
            stdout: stdout);

        var output = stdout.ToString();
        output.ShouldContain("--dry-run");
        output.ShouldContain("Manifest JSON:");
        output.ShouldContain("\"name\":\"Spring Voyage (dry)\"");
        output.ShouldContain("https://github.com/settings/apps/new?manifest=");
    }

    [Fact]
    public async Task RunAsync_DryRun_WithOrg_UsesOrgCreationPath()
    {
        var stdout = new StringWriter();
        await GitHubAppCommand.RunAsync(
            name: "Spring Voyage",
            org: "cvoya-com",
            webhookUrlOverride: "https://example.com/api/v1/webhooks/github",
            writeEnv: false,
            writeSecrets: false,
            envFilePathOverride: null,
            dryRun: true,
            callbackTimeout: TimeSpan.FromSeconds(30),
            cancellationToken: CancellationToken.None,
            stdout: stdout);

        stdout.ToString().ShouldContain("https://github.com/organizations/cvoya-com/settings/apps/new?manifest=");
    }

    [Fact]
    public async Task RunAsync_RejectsBothWriteModes()
    {
        await Should.ThrowAsync<GitHubAppRegisterException>(async () =>
        {
            await GitHubAppCommand.RunAsync(
                name: "x",
                org: null,
                webhookUrlOverride: "https://example.com/api/v1/webhooks/github",
                writeEnv: true,
                writeSecrets: true,
                envFilePathOverride: null,
                dryRun: false,
                callbackTimeout: TimeSpan.FromSeconds(30),
                cancellationToken: CancellationToken.None);
        });
    }

    [Fact(Timeout = 30_000)]
    public async Task RunAsync_HappyPath_ExchangesCode_WritesEnvFile()
    {
        // Mock the conversions endpoint. The CLI hits
        // {base}/app-manifests/{code}/conversions — we stand up a
        // tiny HTTP listener that returns canned App credentials.
        using var mockGitHub = await MockGitHubServer.StartAsync(
            responseJson: """
                {
                  "id": 42,
                  "slug": "spring-voyage-test",
                  "name": "Spring Voyage (test)",
                  "pem": "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----",
                  "webhook_secret": "whsec_42",
                  "client_id": "Iv1.abcd1234",
                  "client_secret": "client-secret-body",
                  "html_url": "https://github.com/apps/spring-voyage-test"
                }
                """,
            statusCode: HttpStatusCode.Created);

        // Tempfile for --write-env output.
        var envDir = Path.Combine(Path.GetTempPath(), $"spring-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(envDir);
        var envPath = Path.Combine(envDir, "spring.env");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("spring-cli-test/1.0");

        var stdout = new StringWriter();

        // The browser-opener replacement triggers GitHub's redirect
        // against our CLI listener. We can't know the CLI's ephemeral
        // port here — but RunAsync binds the listener, bakes the URL
        // containing that port, then invokes the opener with the full
        // URL. So the opener can extract the port from the URL it was
        // given and POST our canned `?code=...` back to that same port.
        static async Task FakeBrowser(string creationUrl)
        {
            // The creation URL contains `redirect_url` encoded inside
            // the base64 manifest. Easier to just re-parse the callback
            // out of the manifest base64 payload.
            var manifestBase64Encoded = creationUrl.Substring(creationUrl.IndexOf("manifest=", StringComparison.Ordinal) + "manifest=".Length);
            var manifestBase64 = Uri.UnescapeDataString(manifestBase64Encoded);
            var manifestJson = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(manifestBase64));
            using var doc = System.Text.Json.JsonDocument.Parse(manifestJson);
            var callback = doc.RootElement.GetProperty("redirect_url").GetString()!;

            // Small delay so the listener is blocked in GetContextAsync
            // when the callback hits.
            await Task.Delay(100);
            using var h = new HttpClient();
            using var _ = await h.GetAsync($"{callback.TrimEnd('/')}/?code=happy-path-code");
        }

        try
        {
            await GitHubAppCommand.RunAsync(
                name: "Spring Voyage (test)",
                org: null,
                webhookUrlOverride: "https://example.com/api/v1/webhooks/github",
                writeEnv: true,
                writeSecrets: false,
                envFilePathOverride: envPath,
                dryRun: false,
                callbackTimeout: TimeSpan.FromSeconds(20),
                cancellationToken: CancellationToken.None,
                httpClientOverride: http,
                githubApiBaseUrlOverride: mockGitHub.BaseUrl,
                browserOpenerOverride: FakeBrowser,
                stdout: stdout);

            // Mock received exactly the expected exchange.
            mockGitHub.ReceivedPath.ShouldBe("/app-manifests/happy-path-code/conversions");
            mockGitHub.ReceivedMethod.ShouldBe("POST");

            // Credentials landed in the env file.
            var envContents = await File.ReadAllTextAsync(envPath, TestContext.Current.CancellationToken);
            envContents.ShouldContain("GitHub__AppId=42");
            envContents.ShouldContain("GitHub__AppSlug=spring-voyage-test");
            envContents.ShouldContain("GitHub__WebhookSecret=whsec_42");
            envContents.ShouldContain("GitHub__PrivateKeyPem=-----BEGIN PRIVATE KEY-----\\nAAAA\\n-----END PRIVATE KEY-----");

            // Success message printed.
            var output = stdout.ToString();
            output.ShouldContain("GitHub App registered.");
            output.ShouldContain("https://github.com/apps/spring-voyage-test/installations/new");
        }
        finally
        {
            Directory.Delete(envDir, recursive: true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task RunAsync_BrowserNeverRedirects_TimesOutWithResumableError()
    {
        using var mockGitHub = await MockGitHubServer.StartAsync(
            responseJson: "{}",
            statusCode: HttpStatusCode.Created);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("spring-cli-test/1.0");

        // The opener is a no-op → nothing arrives on the callback
        // listener → timeout fires.
        static Task NoOpOpener(string _) => Task.CompletedTask;

        var ex = await Should.ThrowAsync<GitHubAppRegisterException>(async () =>
        {
            await GitHubAppCommand.RunAsync(
                name: "Spring Voyage (test)",
                org: null,
                webhookUrlOverride: "https://example.com/api/v1/webhooks/github",
                writeEnv: true,
                writeSecrets: false,
                envFilePathOverride: Path.Combine(Path.GetTempPath(), $"never-used-{Guid.NewGuid()}.env"),
                dryRun: false,
                callbackTimeout: TimeSpan.FromMilliseconds(500),
                cancellationToken: CancellationToken.None,
                httpClientOverride: http,
                githubApiBaseUrlOverride: mockGitHub.BaseUrl,
                browserOpenerOverride: NoOpOpener);
        });

        ex.ExitCode.ShouldBe(2);
        ex.Message.ShouldContain("Timed out");
        ex.Message.ShouldContain("Re-run");
    }
}