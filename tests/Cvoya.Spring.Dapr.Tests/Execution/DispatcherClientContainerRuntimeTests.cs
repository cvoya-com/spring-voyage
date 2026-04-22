// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DispatcherClientContainerRuntime"/> — the
/// HTTP client adapter the worker binds as its only <see cref="IContainerRuntime"/>.
/// </summary>
public class DispatcherClientContainerRuntimeTests
{
    [Fact]
    public async Task RunAsync_ForwardsConfigAsJsonAndMapsResponse()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHandler(async (req, _) =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    id = "container-1",
                    exitCode = 0,
                    stdout = "hello",
                    stderr = "",
                }),
            };
        });

        var runtime = CreateRuntime(handler);

        var config = new ContainerConfig(
            Image: "alpine:latest",
            EnvironmentVariables: new Dictionary<string, string> { ["KEY"] = "value" },
            VolumeMounts: ["/tmp/a:/workspace"],
            WorkingDirectory: "/workspace");

        var result = await runtime.RunAsync(config, TestContext.Current.CancellationToken);

        result.ContainerId.ShouldBe("container-1");
        result.ExitCode.ShouldBe(0);
        result.StandardOutput.ShouldBe("hello");

        captured.ShouldNotBeNull();
        captured!.Method.ShouldBe(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.ShouldBe("/v1/containers");
        captured.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        captured.Headers.Authorization.Parameter.ShouldBe("test-token");

        var body = await captured.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("image").GetString().ShouldBe("alpine:latest");
        parsed.RootElement.GetProperty("detached").GetBoolean().ShouldBeFalse();
        parsed.RootElement.GetProperty("workdir").GetString().ShouldBe("/workspace");
    }

    [Fact]
    public async Task StartAsync_SendsDetachedTrueAndReturnsId()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHandler(async (req, _) =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = "persistent-9" }),
            };
        });

        var runtime = CreateRuntime(handler);
        var id = await runtime.StartAsync(
            new ContainerConfig(Image: "agent:latest"),
            TestContext.Current.CancellationToken);

        id.ShouldBe("persistent-9");

        captured.ShouldNotBeNull();
        var body = await captured!.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("detached").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task StopAsync_IssuesDelete()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHandler(async (req, _) =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        var runtime = CreateRuntime(handler);
        await runtime.StopAsync("container-to-stop", TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.Method.ShouldBe(HttpMethod.Delete);
        captured.RequestUri!.AbsolutePath.ShouldBe("/v1/containers/container-to-stop");
    }

    [Fact]
    public async Task StopAsync_404IsTreatedAsNoOp()
    {
        var handler = new FakeHandler(async (_, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var runtime = CreateRuntime(handler);

        // Should not throw.
        await runtime.StopAsync("already-gone", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_DispatcherError_ThrowsInvalidOperation()
    {
        var handler = new FakeHandler(async (_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom"),
            });

        var runtime = CreateRuntime(handler);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await runtime.RunAsync(
                new ContainerConfig(Image: "x:1"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_SerialisesWorkspaceField_WhenContainerConfigCarriesOne()
    {
        // Issue #1042: ContainerConfig.Workspace must round-trip into the wire
        // body so the dispatcher service has the file map it needs to
        // materialise the workspace on its host filesystem.
        HttpRequestMessage? captured = null;
        var handler = new FakeHandler(async (req, _) =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = "ws-1", exitCode = 0, stdout = "", stderr = "" }),
            };
        });

        var runtime = CreateRuntime(handler);

        var config = new ContainerConfig(
            Image: "claude-code:latest",
            WorkingDirectory: "/workspace",
            Workspace: new ContainerWorkspace(
                MountPath: "/workspace",
                Files: new Dictionary<string, string>
                {
                    ["CLAUDE.md"] = "system prompt",
                    [".mcp.json"] = "{}",
                }));

        await runtime.RunAsync(config, TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        var body = await captured!.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var parsed = JsonDocument.Parse(body);
        var workspace = parsed.RootElement.GetProperty("workspace");
        workspace.GetProperty("mountPath").GetString().ShouldBe("/workspace");
        var files = workspace.GetProperty("files");
        files.GetProperty("CLAUDE.md").GetString().ShouldBe("system prompt");
        files.GetProperty(".mcp.json").GetString().ShouldBe("{}");
    }

    [Fact]
    public async Task RunAsync_MissingBaseUrl_Throws()
    {
        var handler = new FakeHandler(async (_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var runtime = CreateRuntime(handler, baseUrl: null);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await runtime.RunAsync(
                new ContainerConfig(Image: "x:1"),
                TestContext.Current.CancellationToken));
    }

    private static DispatcherClientContainerRuntime CreateRuntime(
        FakeHandler handler,
        string? baseUrl = "http://dispatcher.test/")
    {
        var options = Options.Create(new DispatcherClientOptions
        {
            BaseUrl = baseUrl,
            BearerToken = "test-token",
        });

        var factory = new FakeHttpClientFactory(handler);
        return new DispatcherClientContainerRuntime(factory, options, NullLoggerFactory.Instance);
    }

    private sealed class FakeHttpClientFactory(FakeHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
}