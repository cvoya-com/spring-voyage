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

[Collection(ConsoleRedirectionCollection.Name)]
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
        output.ShouldContain("Manifest JSON");
        output.ShouldContain("\"name\":\"Spring Voyage (dry)\"");
        // The dry-run prints GitHub's POST target (with the CSRF nonce), not a
        // base64 ?manifest= GET URL — GitHub's manifest flow has no GET variant.
        output.ShouldContain("https://github.com/settings/apps/new?state=");
        output.ShouldNotContain("?manifest=");
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

        stdout.ToString().ShouldContain("https://github.com/organizations/cvoya-com/settings/apps/new?state=");
    }

    [Fact]
    public async Task RunAsync_DryRun_OAuthCallbackOverride_LandsInCallbackUrls()
    {
        var stdout = new StringWriter();
        await GitHubAppCommand.RunAsync(
            name: "x",
            org: null,
            webhookUrlOverride: "https://sv.example.com/api/v1/webhooks/github",
            writeEnv: false,
            writeSecrets: false,
            envFilePathOverride: null,
            dryRun: true,
            callbackTimeout: TimeSpan.FromSeconds(5),
            cancellationToken: CancellationToken.None,
            stdout: stdout,
            oauthCallbackUrlOverride: "https://sv.example.com/api/v1/tenant/connectors/github/oauth/callback");

        // callback_urls in the manifest carries the OAuth-callback override —
        // distinct from the webhook URL — so the App's user-OAuth callback
        // matches the connector's GitHub__OAuth__RedirectUri on a real deployment.
        stdout.ToString().ShouldContain(
            "\"callback_urls\":[\"https://sv.example.com/api/v1/tenant/connectors/github/oauth/callback\"]");
    }

    [Fact(Timeout = 30_000)]
    public async Task RunAsync_Manual_ExchangesPastedCode_WritesEnvFileAndForm()
    {
        // The no-browser flow: no listener, no browser. The CLI writes a
        // pre-filled form next to spring.env and reads the code the operator
        // pastes back from GitHub's redirect URL.
        using var mockGitHub = await MockGitHubServer.StartAsync(
            responseJson: """
                {
                  "id": 7,
                  "slug": "sv-manual",
                  "name": "Spring Voyage (manual)",
                  "pem": "-----BEGIN PRIVATE KEY-----\nBB\n-----END PRIVATE KEY-----",
                  "webhook_secret": "whsec_manual",
                  "client_id": "Iv1.manual",
                  "client_secret": "manual-secret",
                  "html_url": "https://github.com/apps/sv-manual"
                }
                """,
            statusCode: HttpStatusCode.Created);

        var envDir = Path.Combine(Path.GetTempPath(), $"spring-manual-{Guid.NewGuid()}");
        Directory.CreateDirectory(envDir);
        var envPath = Path.Combine(envDir, "spring.env");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("spring-cli-test/1.0");
        var stdout = new StringWriter();

        // The operator pastes the WHOLE redirect URL copied from the address
        // bar; the CLI must extract the bare code from it.
        static Task<string?> PasteRedirectUrl() =>
            Task.FromResult<string?>("http://127.0.0.1:1/?code=manual-code-123&state=abc");

        try
        {
            await GitHubAppCommand.RunAsync(
                name: "Spring Voyage (manual)",
                org: null,
                webhookUrlOverride: "https://sv.example.com/api/v1/webhooks/github",
                writeEnv: true,
                writeSecrets: false,
                envFilePathOverride: envPath,
                dryRun: false,
                callbackTimeout: TimeSpan.FromSeconds(20),
                cancellationToken: CancellationToken.None,
                httpClientOverride: http,
                githubApiBaseUrlOverride: mockGitHub.BaseUrl,
                stdout: stdout,
                manual: true,
                codeReaderOverride: PasteRedirectUrl);

            // The pasted full URL was reduced to the bare code for the exchange.
            mockGitHub.ReceivedPath.ShouldBe("/app-manifests/manual-code-123/conversions");
            mockGitHub.ReceivedMethod.ShouldBe("POST");

            var env = await File.ReadAllTextAsync(envPath, TestContext.Current.CancellationToken);
            env.ShouldContain("GitHub__AppId=7");
            env.ShouldContain("GitHub__WebhookSecret=whsec_manual");

            var output = stdout.ToString();
            output.ShouldContain("No browser on this host");

            // The pre-filled POST form is written next to spring.env for the
            // operator to open on a machine that has a browser.
            var formPath = Path.Combine(envDir, "spring-github-app-register.html");
            File.Exists(formPath).ShouldBeTrue();
            var formHtml = await File.ReadAllTextAsync(formPath, TestContext.Current.CancellationToken);
            formHtml.ShouldContain("method=\"post\"");
            formHtml.ShouldContain("github.com/settings/apps/new");
        }
        finally
        {
            Directory.Delete(envDir, recursive: true);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task RunAsync_LoopbackWebhook_NoSecret_ExplainsInsteadOfWarning()
    {
        // A localhost deployment registers the App WITHOUT a webhook, so GitHub
        // returns no webhook_secret. The CLI should explain that calmly, not
        // emit the "omitted fields … Rerun if this looks wrong" warning.
        using var mockGitHub = await MockGitHubServer.StartAsync(
            responseJson: """
                {
                  "id": 9,
                  "slug": "sv-localhost",
                  "name": "Spring Voyage (localhost)",
                  "pem": "-----BEGIN PRIVATE KEY-----\nCC\n-----END PRIVATE KEY-----",
                  "client_id": "Iv1.local",
                  "client_secret": "local-secret",
                  "html_url": "https://github.com/apps/sv-localhost"
                }
                """,
            statusCode: HttpStatusCode.Created);

        var envDir = Path.Combine(Path.GetTempPath(), $"spring-localhost-{Guid.NewGuid()}");
        Directory.CreateDirectory(envDir);
        var envPath = Path.Combine(envDir, "spring.env");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("spring-cli-test/1.0");
        var stdout = new StringWriter();

        static Task<string?> Paste() => Task.FromResult<string?>("loopback-code");

        try
        {
            await GitHubAppCommand.RunAsync(
                name: "Spring Voyage (localhost)",
                org: null,
                webhookUrlOverride: "https://localhost/api/v1/webhooks/github",
                writeEnv: true,
                writeSecrets: false,
                envFilePathOverride: envPath,
                dryRun: false,
                callbackTimeout: TimeSpan.FromSeconds(20),
                cancellationToken: CancellationToken.None,
                httpClientOverride: http,
                githubApiBaseUrlOverride: mockGitHub.BaseUrl,
                stdout: stdout,
                manual: true,
                codeReaderOverride: Paste);

            var output = stdout.ToString();
            output.ShouldContain("GitHub App registered.");
            // Calm explanation for the expected no-webhook case…
            output.ShouldContain("no webhook was registered");
            // …and NOT the alarming omitted-fields warning for the missing secret.
            output.ShouldNotContain("Rerun if this looks wrong");
            output.ShouldNotContain("WebhookSecret");
        }
        finally
        {
            Directory.Delete(envDir, recursive: true);
        }
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

        // The browser-opener replacement is handed the loopback page URL
        // (http://127.0.0.1:<port>/). A real browser renders that page,
        // auto-POSTs its manifest form to GitHub, and GitHub redirects back
        // to the same loopback with ?code=&state=. We simulate that: fetch
        // the served form, read the CSRF nonce off its POST action, then hit
        // the loopback with the canned code + echoed state.
        static async Task FakeBrowser(string localUrl)
        {
            // Small delay so the listener is blocked in GetContextAsync
            // when the requests hit.
            await Task.Delay(100);
            using var h = new HttpClient();
            var formHtml = await h.GetStringAsync(localUrl);
            var state = System.Text.RegularExpressions.Regex
                .Match(formHtml, "state=([0-9a-fA-F]+)").Groups[1].Value;
            using var _ = await h.GetAsync($"{localUrl.TrimEnd('/')}/?code=happy-path-code&state={state}");
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

            // Credentials landed in the env file. Plain tokens stay bare (the
            // numeric AppId in particular must not be quoted — quotes break the
            // .NET long binder); the PEM is single-quoted because it carries
            // whitespace, so `source spring.env` no longer runs "RSA" (#2960).
            var envContents = await File.ReadAllTextAsync(envPath, TestContext.Current.CancellationToken);
            envContents.ShouldContain("GitHub__AppId=42");
            envContents.ShouldContain("GitHub__AppSlug=spring-voyage-test");
            envContents.ShouldContain("GitHub__WebhookSecret=whsec_42");
            envContents.ShouldContain(
                "GitHub__PrivateKeyPem='-----BEGIN PRIVATE KEY-----\\nAAAA\\n-----END PRIVATE KEY-----'");

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
