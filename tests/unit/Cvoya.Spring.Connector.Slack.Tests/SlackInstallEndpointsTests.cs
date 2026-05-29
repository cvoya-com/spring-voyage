// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Connector.Slack.Install;
using Cvoya.Spring.Connector.Slack.Provisioning;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// HTTP-shape tests for <c>POST /api/v1/tenant/connectors/slack/install</c>
/// (#2882). Stands up a minimal <see cref="WebApplication"/> with a
/// substituted <see cref="ISlackManifestInstallService"/> and asserts the
/// endpoint's request normalisation (app-name default, host fallback) and
/// outcome mapping (200 shape, 400 on missing token / bad host, 502 +
/// structured <c>code</c> on a Slack manifest failure).
/// </summary>
public sealed class SlackInstallEndpointsTests
{
    [Fact]
    public async Task Install_DryRun_Returns200WithManifest()
    {
        var service = Substitute.For<ISlackManifestInstallService>();
        service.InstallAsync(Arg.Any<SlackManifestInstallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SlackManifestInstallResult(
                ManifestJson: """{"display_information":{"name":"Spring Voyage"}}""",
                DryRun: true,
                AppId: null,
                AuthorizeUrl: null,
                State: null,
                WrittenSecretNames: Array.Empty<string>()));

        await using var host = await StartHostAsync(service);
        using var client = new HttpClient();
        var response = await client.PostAsJsonAsync(
            new Uri(host.BaseUri, "/install"),
            new { appName = "Spring Voyage", svHost = "https://sv.example.com", dryRun = true },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body.GetProperty("dryRun").GetBoolean().ShouldBeTrue();
        body.GetProperty("manifestJson").GetString()!.ShouldContain("Spring Voyage");
        body.GetProperty("appId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Install_HappyPath_Returns200WithAuthorizeUrl_AndDefaultsAppName()
    {
        var service = Substitute.For<ISlackManifestInstallService>();
        service.InstallAsync(Arg.Any<SlackManifestInstallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SlackManifestInstallResult(
                ManifestJson: """{"display_information":{"name":"Spring Voyage"}}""",
                DryRun: false,
                AppId: "A0123456789",
                AuthorizeUrl: "https://slack.com/oauth/v2/authorize?state=st-1",
                State: "st-1",
                WrittenSecretNames: new[] { SlackSecretNames.ClientId, SlackSecretNames.SigningSecret }));

        await using var host = await StartHostAsync(service);
        using var client = new HttpClient();
        // No appName → server defaults it to "Spring Voyage".
        var response = await client.PostAsJsonAsync(
            new Uri(host.BaseUri, "/install"),
            new
            {
                configToken = "xoxe.xoxp-test",
                svHost = "https://sv.example.com",
                dryRun = false,
                clientState = """{"targetOrigin":"https://portal.example"}""",
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body.GetProperty("appId").GetString().ShouldBe("A0123456789");
        body.GetProperty("authorizeUrl").GetString().ShouldBe("https://slack.com/oauth/v2/authorize?state=st-1");
        body.GetProperty("state").GetString().ShouldBe("st-1");

        await service.Received(1).InstallAsync(
            Arg.Is<SlackManifestInstallRequest>(r =>
                r.AppName == "Spring Voyage"
                && r.ConfigToken == "xoxe.xoxp-test"
                && r.SvHost == "https://sv.example.com"
                && !r.DryRun),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Install_NoSvHost_DefaultsToRequestOrigin()
    {
        var service = Substitute.For<ISlackManifestInstallService>();
        service.InstallAsync(Arg.Any<SlackManifestInstallRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SlackManifestInstallResult(
                "{}", true, null, null, null, Array.Empty<string>()));

        await using var host = await StartHostAsync(service);
        using var client = new HttpClient();
        await client.PostAsJsonAsync(
            new Uri(host.BaseUri, "/install"),
            new { dryRun = true },
            TestContext.Current.CancellationToken);

        // The blank host falls back to the public base URL the request
        // arrived on (http://localhost:<port>).
        await service.Received(1).InstallAsync(
            Arg.Is<SlackManifestInstallRequest>(r => r.SvHost.StartsWith("http://localhost", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Install_MissingConfigToken_NotDryRun_Returns400_WithoutCallingService()
    {
        var service = Substitute.For<ISlackManifestInstallService>();

        await using var host = await StartHostAsync(service);
        using var client = new HttpClient();
        var response = await client.PostAsJsonAsync(
            new Uri(host.BaseUri, "/install"),
            new { svHost = "https://sv.example.com", dryRun = false },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await service.DidNotReceive().InstallAsync(
            Arg.Any<SlackManifestInstallRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Install_InvalidSvHost_Returns400_WithoutCallingService()
    {
        var service = Substitute.For<ISlackManifestInstallService>();

        await using var host = await StartHostAsync(service);
        using var client = new HttpClient();
        var response = await client.PostAsJsonAsync(
            new Uri(host.BaseUri, "/install"),
            new { configToken = "xoxe.x", svHost = "not-a-url", dryRun = false },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await service.DidNotReceive().InstallAsync(
            Arg.Any<SlackManifestInstallRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Install_SlackManifestException_Returns502_WithStructuredErrorCode()
    {
        var service = Substitute.For<ISlackManifestInstallService>();
        service.InstallAsync(Arg.Any<SlackManifestInstallRequest>(), Arg.Any<CancellationToken>())
            .Returns<SlackManifestInstallResult>(_ =>
                throw new SlackManifestException(
                    "Slack rejected the manifest validation: invalid_auth.",
                    statusCode: 200,
                    responseBody: """{"ok":false,"error":"invalid_auth"}""",
                    errorCode: "invalid_auth"));

        await using var host = await StartHostAsync(service);
        using var client = new HttpClient();
        var response = await client.PostAsJsonAsync(
            new Uri(host.BaseUri, "/install"),
            new { configToken = "xoxe.expired", svHost = "https://sv.example.com", dryRun = false },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        var body = await ReadJsonAsync(response);
        // The structured Slack error code rides the `code` extension so the
        // wizard can special-case an expired token.
        body.GetProperty("code").GetString().ShouldBe("invalid_auth");
        body.GetProperty("detail").GetString()!.ShouldContain("token");
    }

    // ---- helpers ----

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        return doc.RootElement.Clone();
    }

    private static async Task<HostHandle> StartHostAsync(ISlackManifestInstallService service)
    {
        var port = FreePort();
        var prefix = $"http://localhost:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(prefix);
        builder.Logging.ClearProviders();

        builder.Services.AddRouting();
        builder.Services.AddSingleton(service);

        var app = builder.Build();
        app.UseRouting();
        ((IEndpointRouteBuilder)app).MapSlackInstallEndpoints();
        await app.StartAsync();
        return new HostHandle(app, new Uri(prefix + "/"));
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record HostHandle(WebApplication App, Uri BaseUri) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await App.StopAsync(cts.Token); } catch { /* best-effort */ }
            await App.DisposeAsync();
        }
    }
}
