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
using NSubstitute.ExceptionExtensions;

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

    // ADR-0055: the dispatcher no longer materialises workspace files.
    // Removed:
    //   PostContainers_WithWorkspace_MaterialisesFilesAndAppendsBindMount
    //   PostContainers_WithEmptyWorkspaceAndNoExplicitWorkdir_LeavesWorkdirUnset
    //   PostContainers_WithWorkspace_RejectsTraversalPaths
    //   PostContainers_DetachedWithWorkspace_DefersCleanupUntilStop
    // The agent-sidecar pulls the bundle from the worker and writes files
    // under the per-member workspace volume; there is no `workspace` field
    // on RunContainerRequest, no DispatcherOptions.WorkspaceRoot, and no
    // ::/workspace bind mount the dispatcher manages.

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
    public async Task PostContainerProbe_ProbeToolMissing_Returns422WithCode()
    {
        // #3085: when the workload image ships no `curl`, the runtime probe
        // raises ContainerProbeToolMissingException. The endpoint must surface
        // it as a distinct 422 + machine-readable code so the worker can
        // reconstruct the typed exception and fast-fail the readiness wait
        // rather than mis-reading it as a transient not-ready boolean.
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .ProbeContainerHttpAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw ContainerProbeToolMissingException.ForCurl(
                image: "byoi:1", stderr: "exec: \"curl\": executable file not found"));

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync(
            "/v1/containers/byoi-1/probe",
            new { url = "http://localhost:8999/.well-known/agent.json" },
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("code").GetString().ShouldBe(ContainersEndpoints.ProbeToolMissingCode);
        body.GetProperty("message").GetString()!.ShouldContain("curl");
    }

    [Fact]
    public async Task GetContainerState_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(
            "/v1/containers/agent-1/state",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetContainerState_RunningContainer_Returns200()
    {
        // #3085: the readiness wait reads liveness from the runtime's inspect
        // metadata (no in-container tooling). A running container reports
        // running=true, exitCode null.
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .GetContainerStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerRunState(IsRunning: true, ExitCode: null, Status: "running"));

        var client = CreateAuthorizedClient();

        var response = await client.GetAsync(
            "/v1/containers/agent-1/state",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("running").GetBoolean().ShouldBeTrue();
        body.GetProperty("status").GetString().ShouldBe("running");

        await _factory.ContainerRuntime.Received(1).GetContainerStateAsync(
            "agent-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetContainerState_ExitedContainer_Returns200WithExitCode()
    {
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .GetContainerStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerRunState(IsRunning: false, ExitCode: 1, Status: "exited"));

        var client = CreateAuthorizedClient();

        var response = await client.GetAsync(
            "/v1/containers/agent-1/state",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("running").GetBoolean().ShouldBeFalse();
        body.GetProperty("exitCode").GetInt32().ShouldBe(1);
        body.GetProperty("status").GetString().ShouldBe("exited");
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

    // ADR-0055: PostContainers_WithWorkspace_PreservesExistingMounts removed;
    // there is no `workspace` field on RunContainerRequest anymore — the
    // per-member workspace volume is mounted via the regular `mounts`
    // (VolumeMounts) field, which other tests cover.

    // ── WaitForExit tests (issue #2198) ────────────────────────────────────

    [Fact]
    public async Task PostContainerWaitForExit_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync(
            "/v1/containers/abc/wait-for-exit",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostContainerWaitForExit_Authorized_LongPollsAndReturnsResult()
    {
        // #2198 added the wait-for-exit primitive so the worker-side
        // ContainerLifecycleManager can decompose Run into Start +
        // probe-daprd-via-exec + Wait. The dispatcher long-holds the
        // request until `podman wait` returns, then echoes exit code +
        // captured stdout/stderr.
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .WaitForExitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("app-container-1", 0, "ok", string.Empty));

        var client = CreateAuthorizedClient();

        var response = await client.PostAsync(
            "/v1/containers/app-container-1/wait-for-exit",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("id").GetString().ShouldBe("app-container-1");
        body.GetProperty("exitCode").GetInt32().ShouldBe(0);
        body.GetProperty("stdout").GetString().ShouldBe("ok");

        await _factory.ContainerRuntime.Received(1).WaitForExitAsync(
            "app-container-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PostContainerWaitForExit_UnknownContainer_Returns404()
    {
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .WaitForExitAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(
                "podman wait missing failed with exit code 1. Stderr: no container with name or ID \"missing\" found"));

        var client = CreateAuthorizedClient();

        var response = await client.PostAsync(
            "/v1/containers/missing/wait-for-exit",
            content: null,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── GetHealth tests (issue #1079) ──────────────────────────────────────

    [Fact]
    public async Task GetContainerHealth_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(
            "/v1/containers/agent-1/health",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetContainerHealth_UnknownContainer_Returns404()
    {
        // IContainerRuntime.GetHealthAsync throws InvalidOperationException
        // for unknown containers; the endpoint must surface that as a 404.
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .GetHealthAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ContainerHealth>(_ =>
                throw new InvalidOperationException("Container 'missing-1' is not known to the runtime."));

        var client = CreateAuthorizedClient();

        var response = await client.GetAsync(
            "/v1/containers/missing-1/health",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("code").GetString().ShouldBe("container_not_found");

        await _factory.ContainerRuntime.Received(1).GetHealthAsync(
            "missing-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetContainerHealth_HealthyContainer_Returns200WithInspectMethod()
    {
        // A container whose HEALTHCHECK status is "healthy" (or has no
        // HEALTHCHECK declared) should yield HTTP 200 with status="healthy"
        // and method="inspect".
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .GetHealthAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerHealth(Healthy: true, Detail: "healthy"));

        var client = CreateAuthorizedClient();

        var response = await client.GetAsync(
            "/v1/containers/agent-1/health",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("status").GetString().ShouldBe("healthy");
        body.GetProperty("method").GetString().ShouldBe("inspect");
        // checkedAt must be present and parseable as a date-time offset.
        body.TryGetProperty("checkedAt", out var checkedAt).ShouldBeTrue();
        DateTimeOffset.TryParse(checkedAt.GetString(), out _).ShouldBeTrue();
        // reason must be absent on a healthy result (or null / empty).
        if (body.TryGetProperty("reason", out var reason))
        {
            reason.ValueKind.ShouldBe(JsonValueKind.Null);
        }

        await _factory.ContainerRuntime.Received(1).GetHealthAsync(
            "agent-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetContainerHealth_UnhealthyContainer_Returns503WithReason()
    {
        // A container whose HEALTHCHECK reports "unhealthy" should yield HTTP 503
        // so standard HTTP health-check consumers can detect failure without
        // parsing the body. The body carries the raw inspect status as "reason".
        _factory.ContainerRuntime.ClearSubstitute();
        _factory.ContainerRuntime
            .GetHealthAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerHealth(Healthy: false, Detail: "unhealthy"));

        var client = CreateAuthorizedClient();

        var response = await client.GetAsync(
            "/v1/containers/agent-1/health",
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        body.GetProperty("status").GetString().ShouldBe("unhealthy");
        body.GetProperty("reason").GetString().ShouldBe("unhealthy");
        body.TryGetProperty("checkedAt", out var checkedAt2).ShouldBeTrue();
        DateTimeOffset.TryParse(checkedAt2.GetString(), out _).ShouldBeTrue();

        await _factory.ContainerRuntime.Received(1).GetHealthAsync(
            "agent-1", Arg.Any<CancellationToken>());
    }
}
