// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Core.Execution;

using NSubstitute;
using NSubstitute.ClearExtensions;

using Shouldly;

using Xunit;

public class ContainersEndpointsTests : IClassFixture<DispatcherWebApplicationFactory>
{
    private readonly DispatcherWebApplicationFactory _factory;

    public ContainersEndpointsTests(DispatcherWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateAuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", DispatcherWebApplicationFactory.ValidToken);
        return client;
    }

    [Fact]
    public async Task PostContainers_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/containers", new
        {
            image = "alpine:latest",
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostContainers_WithUnknownToken_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not-a-real-token");

        var response = await client.PostAsJsonAsync("/v1/containers", new
        {
            image = "alpine:latest",
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostContainers_MissingImage_Returns400()
    {
        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/containers", new
        {
            image = "",
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostContainers_BlockingRun_ReturnsRuntimeResult()
    {
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("abc123", 0, "ok", string.Empty));

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/containers", new
        {
            image = "alpine:latest",
            env = new Dictionary<string, string> { ["FOO"] = "bar" },
            mounts = new[] { "/tmp/a:/workspace" },
            workdir = "/workspace",
            detached = false,
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("id").GetString().ShouldBe("abc123");
        body.GetProperty("exitCode").GetInt32().ShouldBe(0);

        await _factory.ContainerRuntime.Received(1).RunAsync(
            Arg.Is<ContainerConfig>(c =>
                c.Image == "alpine:latest"
                && c.EnvironmentVariables!["FOO"] == "bar"
                && c.VolumeMounts!.Contains("/tmp/a:/workspace")
                && c.WorkingDirectory == "/workspace"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostContainers_Detached_CallsStartAsync()
    {
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .StartAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns("persistent-xyz");

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/containers", new
        {
            image = "agent:latest",
            detached = true,
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("id").GetString().ShouldBe("persistent-xyz");

        await _factory.ContainerRuntime.Received(1).StartAsync(
            Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
        await _factory.ContainerRuntime.DidNotReceive().RunAsync(
            Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteContainer_Authorized_CallsStopAsync()
    {
        _factory.ContainerRuntime.ClearSubstitute();
        var client = CreateAuthorizedClient();

        var response = await client.DeleteAsync("/v1/containers/abc123", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await _factory.ContainerRuntime.Received(1).StopAsync("abc123", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteContainer_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.DeleteAsync("/v1/containers/abc", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_UnAuthenticated_ReturnsOk()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostContainers_WithWorkspace_MaterialisesFilesAndAppendsBindMount()
    {
        _factory.ContainerRuntime.ClearSubstitute();

        ContainerConfig? captured = null;
        _factory.ContainerRuntime
            .RunAsync(Arg.Do<ContainerConfig>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("ws-blocking", 0, "ok", string.Empty));

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/containers", new
        {
            image = "claude-code:latest",
            workspace = new
            {
                mountPath = "/workspace",
                files = new Dictionary<string, string>
                {
                    ["CLAUDE.md"] = "system prompt body",
                    [".mcp.json"] = "{\"mcpServers\":{}}",
                    ["nested/dir/note.txt"] = "nested",
                },
            },
            detached = false,
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        captured.ShouldNotBeNull();
        captured!.WorkingDirectory.ShouldBe("/workspace");
        var bindMount = captured.VolumeMounts!.Single();
        bindMount.ShouldEndWith(":/workspace");

        var hostDir = bindMount[..bindMount.LastIndexOf(":/workspace", StringComparison.Ordinal)];
        Directory.Exists(hostDir).ShouldBeFalse(
            "blocking runs must clean the materialised dir up after the runtime returns");
        // The dir was materialised inside the configured root before being deleted.
        hostDir.ShouldStartWith(_factory.WorkspaceRoot);
    }

    [Fact]
    public async Task PostContainers_WithWorkspace_RejectsTraversalPaths()
    {
        _factory.ContainerRuntime.ClearSubstitute();

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/containers", new
        {
            image = "claude-code:latest",
            workspace = new
            {
                mountPath = "/workspace",
                files = new Dictionary<string, string>
                {
                    ["../../etc/passwd"] = "x",
                },
            },
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _factory.ContainerRuntime.DidNotReceive().RunAsync(
            Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostContainers_DetachedWithWorkspace_DefersCleanupUntilStop()
    {
        _factory.ContainerRuntime.ClearSubstitute();

        ContainerConfig? captured = null;
        _factory.ContainerRuntime
            .StartAsync(Arg.Do<ContainerConfig>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns("persistent-ws-1");

        var client = CreateAuthorizedClient();

        var startResponse = await client.PostAsJsonAsync("/v1/containers", new
        {
            image = "agent:latest",
            workspace = new
            {
                mountPath = "/workspace",
                files = new Dictionary<string, string> { ["A.txt"] = "alpha" },
            },
            detached = true,
        }, TestContext.Current.CancellationToken);

        startResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        captured.ShouldNotBeNull();
        var bindMount = captured!.VolumeMounts!.Single();
        var hostDir = bindMount[..bindMount.LastIndexOf(":/workspace", StringComparison.Ordinal)];
        Directory.Exists(hostDir).ShouldBeTrue(
            "detached starts must keep the workspace until DELETE is called");
        File.ReadAllText(Path.Combine(hostDir, "A.txt")).ShouldBe("alpha");

        var deleteResponse = await client.DeleteAsync(
            "/v1/containers/persistent-ws-1", TestContext.Current.CancellationToken);
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        Directory.Exists(hostDir).ShouldBeFalse(
            "DELETE should sweep the workspace tracked by the detached start");
    }

    [Fact]
    public async Task PostContainers_WithWorkspace_PreservesExistingMounts()
    {
        _factory.ContainerRuntime.ClearSubstitute();

        ContainerConfig? captured = null;
        _factory.ContainerRuntime
            .RunAsync(Arg.Do<ContainerConfig>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("ws-with-extra", 0, string.Empty, string.Empty));

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/containers", new
        {
            image = "claude-code:latest",
            mounts = new[] { "/var/run/secrets:/secrets:ro" },
            workspace = new
            {
                mountPath = "/workspace",
                files = new Dictionary<string, string> { ["CLAUDE.md"] = "x" },
            },
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        captured.ShouldNotBeNull();
        captured!.VolumeMounts!.Count.ShouldBe(2);
        captured.VolumeMounts.ShouldContain("/var/run/secrets:/secrets:ro");
        captured.VolumeMounts.Last().ShouldEndWith(":/workspace");
    }
}