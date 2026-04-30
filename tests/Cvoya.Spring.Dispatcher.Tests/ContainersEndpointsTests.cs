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
    public async Task PostContainers_AdditionalNetworks_RoundTripIntoContainerConfig()
    {
        // ADR 0028 / issue #1166: ContainerLifecycleManager dual-attaches
        // workflow / unit containers to the per-tenant bridge. The wire
        // shape carries the extras as `additionalNetworks`; the dispatcher
        // must forward them onto ContainerConfig.AdditionalNetworks so the
        // process runtime emits the second `--network` flag.
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("net-1", 0, string.Empty, string.Empty));

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/containers", new
        {
            image = "agent:v1",
            network = "spring-net-abc",
            additionalNetworks = new[] { "spring-tenant-default" },
            detached = false,
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await _factory.ContainerRuntime.Received(1).RunAsync(
            Arg.Is<ContainerConfig>(c =>
                c.NetworkName == "spring-net-abc"
                && c.AdditionalNetworks != null
                && c.AdditionalNetworks.Count == 1
                && c.AdditionalNetworks[0] == "spring-tenant-default"),
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
    public async Task PostContainers_WithEmptyWorkspaceAndNoExplicitWorkdir_LeavesWorkdirUnset()
    {
        // Regression for #1159 (dispatcher side): launchers like
        // DaprAgentLauncher bind-mount an empty workspace to keep the launch
        // shape uniform with file-bearing launchers, but ship images whose
        // CMD is relative to the image WORKDIR (e.g. `python agent.py` from
        // /app). If the dispatcher silently defaults workdir to the
        // materialised mount path, the relative CMD lookup fails and the
        // container exits immediately with "No such file or directory".
        // The dispatcher must only override the workdir when the workspace
        // actually carries files, mirroring the worker-side policy in
        // ContainerConfigBuilder.Build.
        _factory.ContainerRuntime.ClearSubstitute();

        ContainerConfig? captured = null;
        _factory.ContainerRuntime
            .RunAsync(Arg.Do<ContainerConfig>(c => captured = c), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("ws-empty", 0, "ok", string.Empty));

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/containers", new
        {
            image = "spring-voyage-agent-dapr:latest",
            workspace = new
            {
                mountPath = "/workspace",
                files = new Dictionary<string, string>(),
            },
            detached = false,
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        captured.ShouldNotBeNull();
        captured!.WorkingDirectory.ShouldBeNull(
            "an empty workspace must not override the image's default WORKDIR");
        captured.VolumeMounts.ShouldNotBeNull();
        captured.VolumeMounts!.ShouldContain(m => m.EndsWith(":/workspace"));
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
    public async Task PostContainerProbe_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/containers/abc/probe",
            new { url = "http://localhost:3500/v1.0/healthz" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostContainerProbe_MissingUrl_Returns400()
    {
        _factory.ContainerRuntime.ClearSubstitute();

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            "/v1/containers/abc/probe",
            new { url = "" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _factory.ContainerRuntime.DidNotReceive().ProbeContainerHttpAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostContainerProbe_Authorized_ReturnsHealthyJson()
    {
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .ProbeContainerHttpAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            "/v1/containers/sidecar-1/probe",
            new { url = "http://localhost:3500/v1.0/healthz" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("healthy").GetBoolean().ShouldBeTrue();

        await _factory.ContainerRuntime.Received(1).ProbeContainerHttpAsync(
            "sidecar-1",
            "http://localhost:3500/v1.0/healthz",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostContainerProbe_RuntimeReturnsFalse_ReportsUnhealthy()
    {
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .ProbeContainerHttpAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            "/v1/containers/sidecar-1/probe",
            new { url = "http://localhost:3500/v1.0/healthz" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        // The probe surface deliberately collapses every failure mode
        // (timeout, missing wget, exited container, non-2xx) into a single
        // boolean — the worker's polling loop is the sole owner of retry
        // semantics. This test pins that bit instead of accidentally
        // upgrading negative answers to 5xx.
        body.GetProperty("healthy").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task PostContainerA2A_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/containers/abc/a2a",
            new { url = "http://localhost:8999/", bodyBase64 = string.Empty },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostContainerA2A_MissingUrl_Returns400()
    {
        // ADR 0028 / #1160: the A2A proxy endpoint is the worker's only
        // way to reach an agent across the platform/tenant network split.
        // Reject malformed requests at the edge so a future caller bug
        // surfaces here rather than as a confusing wget exec failure
        // inside the container.
        _factory.ContainerRuntime.ClearSubstitute();

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            "/v1/containers/abc/a2a",
            new { url = "", bodyBase64 = string.Empty },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _factory.ContainerRuntime.DidNotReceive().SendHttpJsonAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostContainerA2A_Authorized_ForwardsBodyAndReturnsBase64Response()
    {
        // Pin the wire shape — base64 in / base64 out — and that the
        // dispatcher hands the request to IContainerRuntime.SendHttpJsonAsync
        // verbatim. The base64 hop exists so the worker can ship a JSON
        // payload through JSON-on-the-wire without escaping headaches and
        // so the dispatcher can pipe the bytes straight to wget's stdin.
        _factory.ContainerRuntime.ClearSubstitute();
        var responseBytes = "{\"jsonrpc\":\"2.0\",\"result\":{\"task\":{}}}"u8.ToArray();
        byte[]? capturedBody = null;
        string? capturedUrl = null;
        string? capturedContainerId = null;
        _factory.ContainerRuntime
            .SendHttpJsonAsync(
                Arg.Do<string>(id => capturedContainerId = id),
                Arg.Do<string>(url => capturedUrl = url),
                Arg.Do<byte[]>(b => capturedBody = b),
                Arg.Any<CancellationToken>())
            .Returns(new ContainerHttpResponse(200, responseBytes));

        var client = CreateAuthorizedClient();
        var requestBytes = "{\"jsonrpc\":\"2.0\",\"method\":\"message/send\"}"u8.ToArray();

        var response = await client.PostAsJsonAsync(
            "/v1/containers/agent-1/a2a",
            new
            {
                url = "http://localhost:8999/",
                bodyBase64 = Convert.ToBase64String(requestBytes),
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("statusCode").GetInt32().ShouldBe(200);
        var roundtripped = Convert.FromBase64String(body.GetProperty("bodyBase64").GetString()!);
        roundtripped.ShouldBe(responseBytes);

        capturedContainerId.ShouldBe("agent-1");
        capturedUrl.ShouldBe("http://localhost:8999/");
        capturedBody.ShouldBe(requestBytes);
    }

    [Fact]
    public async Task PostContainerA2A_RuntimeReturns502_PassesThroughStatus()
    {
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .SendHttpJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerHttpResponse(502, []));

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            "/v1/containers/agent-1/a2a",
            new { url = "http://localhost:8999/", bodyBase64 = string.Empty },
            TestContext.Current.CancellationToken);

        // The HTTP wrapper is always 200 — the proxied status lives in the
        // body so the worker sees the same shape regardless of whether
        // wget succeeded or failed (mirrors the probe endpoint pattern).
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("statusCode").GetInt32().ShouldBe(502);
        body.GetProperty("bodyBase64").GetString().ShouldBe(string.Empty);
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

    // ── ProbeFromHost tests (issue #1175) ──────────────────────────────────

    [Fact]
    public async Task PostContainerProbeFromHost_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/containers/abc/probe-from-host",
            new { url = "http://localhost:8999/.well-known/agent.json" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostContainerProbeFromHost_MissingUrl_Returns400()
    {
        _factory.ContainerRuntime.ClearSubstitute();

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            "/v1/containers/abc/probe-from-host",
            new { url = "" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _factory.ContainerRuntime.DidNotReceive().ProbeHttpFromHostAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostContainerProbeFromHost_Authorized_RuntimeReturnsTrue_ReportsHealthy()
    {
        // Direct path: dispatcher host resolves container IP and issues GET —
        // no wget, no curl inside the container (issue #1175).
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .ProbeHttpFromHostAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            "/v1/containers/agent-1/probe-from-host",
            new { url = "http://localhost:8999/.well-known/agent.json" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("healthy").GetBoolean().ShouldBeTrue();

        await _factory.ContainerRuntime.Received(1).ProbeHttpFromHostAsync(
            "agent-1",
            "http://localhost:8999/.well-known/agent.json",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostContainerProbeFromHost_RuntimeReturnsFalse_ReportsUnhealthy()
    {
        // The boolean-collapse contract matches the existing /probe endpoint:
        // the polling loop owns retry semantics; the dispatcher never upgrades
        // a "not ready yet" answer to an error response.
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .ProbeHttpFromHostAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            "/v1/containers/agent-1/probe-from-host",
            new { url = "http://localhost:8999/.well-known/agent.json" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("healthy").GetBoolean().ShouldBeFalse();
    }
}