// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.GitHubApp;

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Cli.GitHubApp;

using Shouldly;

using Xunit;

public class ManifestConversionClientTests
{
    [Fact]
    public async Task ExchangeCodeAsync_HappyPath_ParsesExpectedFields()
    {
        using var mock = await MockGitHubServer.StartAsync(
            responseJson: """
                {
                  "id": 123456,
                  "slug": "my-spring",
                  "name": "Spring Voyage",
                  "pem": "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----",
                  "webhook_secret": "whsec_xyz",
                  "client_id": "lv1.1234",
                  "client_secret": "secret-body",
                  "html_url": "https://github.com/apps/my-spring",
                  "permissions": { "issues": "read" },
                  "events": ["issues", "pull_request"]
                }
                """,
            statusCode: HttpStatusCode.Created);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("spring-cli-test/1.0");
        var client = new ManifestConversionClient(http, mock.BaseUrl);

        var result = await client.ExchangeCodeAsync("one-time-code", TestContext.Current.CancellationToken);

        result.AppId.ShouldBe(123456L);
        result.Slug.ShouldBe("my-spring");
        result.Name.ShouldBe("Spring Voyage");
        result.Pem!.ShouldContain("BEGIN PRIVATE KEY");
        result.WebhookSecret.ShouldBe("whsec_xyz");
        result.ClientId.ShouldBe("lv1.1234");
        result.ClientSecret.ShouldBe("secret-body");
        result.HtmlUrl.ShouldBe("https://github.com/apps/my-spring");
        result.Permissions!["issues"].ShouldBe("read");
        result.Events!.ShouldContain("issues");

        mock.ReceivedPath.ShouldBe("/app-manifests/one-time-code/conversions");
        mock.ReceivedMethod.ShouldBe("POST");
    }

    [Fact]
    public async Task ExchangeCodeAsync_NameAlreadyTaken_SurfacesGitHubErrorBody()
    {
        using var mock = await MockGitHubServer.StartAsync(
            responseJson: """
                {"message":"Name has already been taken","errors":[{"resource":"Integration","field":"name","code":"already_exists"}]}
                """,
            statusCode: HttpStatusCode.UnprocessableEntity);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("spring-cli-test/1.0");
        var client = new ManifestConversionClient(http, mock.BaseUrl);

        var ex = await Should.ThrowAsync<ManifestConversionException>(async () =>
            await client.ExchangeCodeAsync("expired", TestContext.Current.CancellationToken));

        ex.StatusCode.ShouldBe(422);
        ex.ResponseBody!.ShouldContain("Name has already been taken");
    }

    [Fact]
    public async Task ExchangeCodeAsync_RejectsEmptyCode()
    {
        using var http = new HttpClient();
        var client = new ManifestConversionClient(http, "http://127.0.0.1:0");
        await Should.ThrowAsync<ArgumentException>(async () =>
            await client.ExchangeCodeAsync("", TestContext.Current.CancellationToken));
    }
}

/// <summary>
/// Lightweight HttpListener-backed stub standing in for api.github.com.
/// Serves exactly one request and returns the canned response, then
/// shuts down.
/// </summary>
internal sealed class MockGitHubServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _loop;

    public string BaseUrl { get; }
    public string? ReceivedPath { get; private set; }
    public string? ReceivedMethod { get; private set; }

    private MockGitHubServer(HttpListener listener, string baseUrl, string responseJson, HttpStatusCode status)
    {
        _listener = listener;
        BaseUrl = baseUrl;
        _loop = Task.Run(async () =>
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                ReceivedPath = ctx.Request.Url?.AbsolutePath;
                ReceivedMethod = ctx.Request.HttpMethod;
                var body = System.Text.Encoding.UTF8.GetBytes(responseJson);
                ctx.Response.StatusCode = (int)status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.OutputStream.Close();
            }
            catch (HttpListenerException)
            {
                // Listener shut down before request arrived.
            }
            catch (ObjectDisposedException)
            {
                // Same.
            }
        });
    }

    public static Task<MockGitHubServer> StartAsync(string responseJson, HttpStatusCode statusCode)
    {
        var (listener, port) = CallbackListener.BindHttpListenerWithRetry();
        var baseUrl = $"http://127.0.0.1:{port}";
        return Task.FromResult(new MockGitHubServer(listener, baseUrl, responseJson, statusCode));
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        try { ((IDisposable)_listener).Dispose(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}