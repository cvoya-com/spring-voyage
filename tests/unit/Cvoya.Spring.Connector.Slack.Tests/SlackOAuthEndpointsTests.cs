// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Cvoya.Spring.Connector.Slack.Auth.OAuth;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the HTML postMessage handoff added in issue
/// #2837. The Slack OAuth callback at
/// <c>GET /api/v1/tenant/connectors/slack/oauth/callback</c> now
/// renders an HTML page whose <c>&lt;script&gt;</c> body posts the
/// outcome back to <c>window.opener</c>, mirroring the GitHub
/// connector flow. Tests stand up a minimal <see cref="WebApplication"/>
/// and assert:
/// <list type="bullet">
///   <item><description><c>Content-Type</c> is <c>text/html</c> on every outcome.</description></item>
///   <item><description>The HTML body carries a parseable
///     <c>postMessage</c> call with the expected
///     <c>sv:slack:oauth:done</c> payload.</description></item>
///   <item><description><c>targetOrigin</c> is the concrete portal
///     origin pulled from the OAuth <c>clientState</c>, never <c>*</c>.</description></item>
///   <item><description>When no <c>targetOrigin</c> can be derived,
///     the page omits the <c>postMessage</c> call entirely (the brief's
///     "refuse to render postMessage" requirement).</description></item>
/// </list>
/// </summary>
public sealed class SlackOAuthEndpointsTests
{
    private const string PortalOrigin = "http://localhost:3000";

    [Fact]
    public async Task Callback_SuccessOutcome_RendersHtmlWithSuccessPayloadAndPortalOrigin()
    {
        var service = Substitute.For<ISlackOAuthService>();
        var stateStore = Substitute.For<ISlackOAuthStateStore>();
        service.HandleCallbackAsync("the-code", "the-state", Arg.Any<CancellationToken>())
            .Returns(new SlackCallbackOutcome.Success(
                TeamId: "T-success",
                BotUserId: "U-bot",
                InstallerUserId: "U-installer")
            {
                ClientState = $$"""{"targetOrigin":"{{PortalOrigin}}"}""",
            });

        await using var host = await StartHostAsync(service, stateStore);
        using var client = new HttpClient();
        var response = await client.GetAsync(
            new Uri(host.BaseUri, "/oauth/callback?code=the-code&state=the-state"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var payload = ExtractPostMessagePayload(body);
        payload.ShouldNotBeNull();
        payload!.RootElement.GetProperty("type").GetString().ShouldBe(SlackOAuthEndpoints.CallbackMessageType);
        payload.RootElement.GetProperty("status").GetString().ShouldBe("success");

        // targetOrigin must be the concrete portal origin — NEVER "*".
        ExtractTargetOrigin(body).ShouldBe(PortalOrigin);
        body.ShouldNotContain("'*'");
        body.ShouldNotContain("\"*\"");
    }

    [Fact]
    public async Task Callback_EnterpriseGridOutcome_RendersHtmlWithGridErrorCode()
    {
        var service = Substitute.For<ISlackOAuthService>();
        var stateStore = Substitute.For<ISlackOAuthStateStore>();
        service.HandleCallbackAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SlackCallbackOutcome.EnterpriseGridUnsupported(
                EnterpriseId: "E123",
                Reason: "Slack Enterprise Grid installs are not supported in v0.1.")
            {
                ClientState = $$"""{"targetOrigin":"{{PortalOrigin}}"}""",
            });

        await using var host = await StartHostAsync(service, stateStore);
        using var client = new HttpClient();
        var response = await client.GetAsync(
            new Uri(host.BaseUri, "/oauth/callback?code=c&state=s"),
            TestContext.Current.CancellationToken);

        // HTTP status code is unchanged from the pre-#2837 JSON shape so
        // direct-API callers (CLI / curl) keep the same outcome
        // discrimination. Only the body type changes.
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var payload = ExtractPostMessagePayload(body);
        payload.ShouldNotBeNull();
        payload!.RootElement.GetProperty("status").GetString().ShouldBe("error");
        payload.RootElement.GetProperty("error").GetString().ShouldBe("SlackEnterpriseGridUnsupported");
        payload.RootElement.GetProperty("message").GetString()!.ShouldContain("Enterprise Grid");
        ExtractTargetOrigin(body).ShouldBe(PortalOrigin);
    }

    [Fact]
    public async Task Callback_WorkspaceConflictOutcome_RendersHtmlWithConflictCode()
    {
        var service = Substitute.For<ISlackOAuthService>();
        var stateStore = Substitute.For<ISlackOAuthStateStore>();
        service.HandleCallbackAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SlackCallbackOutcome.WorkspaceConflict(
                ExpectedTeamId: "T-existing",
                ReceivedTeamId: "T-new",
                Reason: "Disconnect the existing binding first.")
            {
                ClientState = $$"""{"targetOrigin":"{{PortalOrigin}}"}""",
            });

        await using var host = await StartHostAsync(service, stateStore);
        using var client = new HttpClient();
        var response = await client.GetAsync(
            new Uri(host.BaseUri, "/oauth/callback?code=c&state=s"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var payload = ExtractPostMessagePayload(body);
        payload.ShouldNotBeNull();
        payload!.RootElement.GetProperty("error").GetString().ShouldBe("SlackWorkspaceConflict");
    }

    [Fact]
    public async Task Callback_ExchangeFailedOutcome_RendersHtmlWith502AndExchangeFailedCode()
    {
        var service = Substitute.For<ISlackOAuthService>();
        var stateStore = Substitute.For<ISlackOAuthStateStore>();
        service.HandleCallbackAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SlackCallbackOutcome.ExchangeFailed("invalid_code")
            {
                ClientState = $$"""{"targetOrigin":"{{PortalOrigin}}"}""",
            });

        await using var host = await StartHostAsync(service, stateStore);
        using var client = new HttpClient();
        var response = await client.GetAsync(
            new Uri(host.BaseUri, "/oauth/callback?code=c&state=s"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var payload = ExtractPostMessagePayload(body);
        payload.ShouldNotBeNull();
        payload!.RootElement.GetProperty("error").GetString().ShouldBe("exchange_failed");
        payload.RootElement.GetProperty("message").GetString()!.ShouldContain("invalid_code");
    }

    [Fact]
    public async Task Callback_InvalidStateOutcome_RendersHtmlWithoutPostMessage()
    {
        // InvalidState means the state token was unknown/expired —
        // there's no consumed entry to surface the targetOrigin off of,
        // so the page falls back to the static "you can close this tab"
        // message and does NOT call postMessage. We assert that omission
        // explicitly: targetOrigin would be a real-host URL, never the
        // wildcard.
        var service = Substitute.For<ISlackOAuthService>();
        var stateStore = Substitute.For<ISlackOAuthStateStore>();
        service.HandleCallbackAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SlackCallbackOutcome.InvalidState());

        await using var host = await StartHostAsync(service, stateStore);
        using var client = new HttpClient();
        var response = await client.GetAsync(
            new Uri(host.BaseUri, "/oauth/callback?code=c&state=unknown"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldNotContain("window.opener.postMessage");
        body.ShouldContain("close this tab");
    }

    [Fact]
    public async Task Callback_UserDeniedConsent_ConsumesStateAndRendersErrorHtml()
    {
        // When Slack appends ?error=access_denied (user clicked Cancel
        // on Slack's consent screen), the service is never invoked. The
        // endpoint must still peek-and-consume the state token directly
        // so it can surface targetOrigin AND invalidate the token.
        var service = Substitute.For<ISlackOAuthService>();
        var stateStore = Substitute.For<ISlackOAuthStateStore>();
        stateStore.ConsumeAsync("state-denied", Arg.Any<CancellationToken>())
            .Returns(new SlackOAuthStateEntry(
                State: "state-denied",
                Scopes: "chat:write",
                RedirectUri: "https://example.test/cb",
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10),
                ClientState: $$"""{"targetOrigin":"{{PortalOrigin}}"}"""));

        await using var host = await StartHostAsync(service, stateStore);
        using var client = new HttpClient();
        var response = await client.GetAsync(
            new Uri(host.BaseUri, "/oauth/callback?error=access_denied&state=state-denied"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var payload = ExtractPostMessagePayload(body);
        payload.ShouldNotBeNull();
        payload!.RootElement.GetProperty("error").GetString().ShouldBe("access_denied");
        ExtractTargetOrigin(body).ShouldBe(PortalOrigin);

        // The state token must be consumed exactly once (no reuse).
        await stateStore.Received(1).ConsumeAsync("state-denied", Arg.Any<CancellationToken>());
        // The service must NEVER be invoked on the user-denied path.
        await service.DidNotReceive().HandleCallbackAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Callback_SuccessWithNoTargetOrigin_RendersFallbackHtmlOnly()
    {
        // ClientState present but missing targetOrigin: still render the
        // HTML page so the user sees the friendly close-this-tab message,
        // but skip the postMessage call entirely. This covers the
        // "config bug" path the brief calls out — the binding has
        // already been persisted; the popup just won't notify its
        // opener synchronously.
        var service = Substitute.For<ISlackOAuthService>();
        var stateStore = Substitute.For<ISlackOAuthStateStore>();
        service.HandleCallbackAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SlackCallbackOutcome.Success("T1", "U-bot", "U-installer")
            {
                ClientState = """{"unrelated":"field"}""",
            });

        await using var host = await StartHostAsync(service, stateStore);
        using var client = new HttpClient();
        var response = await client.GetAsync(
            new Uri(host.BaseUri, "/oauth/callback?code=c&state=s"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldNotContain("window.opener.postMessage");
        body.ShouldContain("close this tab");
    }

    [Fact]
    public void TryReadTargetOrigin_StripsPathAndQueryFromPortalUrl()
    {
        // Sanity-check: even if the portal handed us a URL with a path
        // or query, the helper strips down to the authority — that's the
        // shape window.postMessage's targetOrigin expects ("scheme://host[:port]").
        var origin = SlackOAuthEndpoints.TryReadTargetOrigin(
            """{"targetOrigin":"https://portal.example/settings?tab=connectors"}""");
        origin.ShouldBe("https://portal.example");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("""{"targetOrigin":42}""")]
    [InlineData("""{"targetOrigin":"file:///etc/passwd"}""")]
    [InlineData("""{"targetOrigin":"javascript:alert(1)"}""")]
    [InlineData("""{"unrelated":"field"}""")]
    public void TryReadTargetOrigin_RejectsMalformedOrNonHttpInput(string? clientState)
    {
        SlackOAuthEndpoints.TryReadTargetOrigin(clientState).ShouldBeNull();
    }

    // ---- helpers ----

    /// <summary>
    /// Extracts the literal JSON object handed to
    /// <c>window.opener.postMessage</c> in the rendered HTML. Returns
    /// <c>null</c> when no postMessage call is present (i.e. the page
    /// fell back to the static message).
    /// </summary>
    private static JsonDocument? ExtractPostMessagePayload(string html)
    {
        // The Endpoint inlines the JSON as
        //   window.opener.postMessage({...}, "<origin>");
        // We don't depend on exact whitespace — just match the literal
        // up to the closing brace of the JSON object.
        var match = Regex.Match(
            html,
            @"window\.opener\.postMessage\(\s*(?<payload>\{.*?\})\s*,\s*""(?<origin>[^""]+)""\s*\)",
            RegexOptions.Singleline);
        return match.Success
            ? JsonDocument.Parse(match.Groups["payload"].Value)
            : null;
    }

    /// <summary>
    /// Pulls the literal <c>targetOrigin</c> argument off the
    /// rendered postMessage call. Returns <c>null</c> when no
    /// postMessage call is present.
    /// </summary>
    private static string? ExtractTargetOrigin(string html)
    {
        var match = Regex.Match(
            html,
            @"window\.opener\.postMessage\(\s*\{.*?\}\s*,\s*""(?<origin>[^""]+)""\s*\)",
            RegexOptions.Singleline);
        return match.Success ? match.Groups["origin"].Value : null;
    }

    private static async Task<HostHandle> StartHostAsync(
        ISlackOAuthService service,
        ISlackOAuthStateStore stateStore)
    {
        var port = FreePort();
        var prefix = $"http://localhost:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(prefix);
        builder.Logging.ClearProviders();

        builder.Services.AddRouting();
        builder.Services.AddSingleton(service);
        builder.Services.AddSingleton(stateStore);

        var app = builder.Build();
        app.UseRouting();
        var group = ((IEndpointRouteBuilder)app);
        group.MapSlackOAuthEndpoints();
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
