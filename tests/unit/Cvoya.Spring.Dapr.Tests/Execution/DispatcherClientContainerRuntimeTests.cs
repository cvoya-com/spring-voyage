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
    public async Task RunAsync_SerialisesAdditionalNetworks_WhenContainerConfigCarriesThem()
    {
        // ADR 0028 / issue #1166: ContainerLifecycleManager dual-attaches
        // Dapr-fronted containers to a per-tenant bridge alongside the per-app
        // spring-net-<guid> bridge. The wire shape must carry the extra
        // networks so the dispatcher emits the second `--network` flag.
        HttpRequestMessage? captured = null;
        var handler = new FakeHandler(async (req, _) =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id = "an-1", exitCode = 0, stdout = "", stderr = "" }),
            };
        });

        var runtime = CreateRuntime(handler);

        var config = new ContainerConfig(
            Image: "agent:v1",
            NetworkName: "spring-net-abc",
            AdditionalNetworks: ["spring-tenant-default"]);

        await runtime.RunAsync(config, TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        var body = await captured!.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("network").GetString().ShouldBe("spring-net-abc");
        var extra = parsed.RootElement.GetProperty("additionalNetworks");
        extra.GetArrayLength().ShouldBe(1);
        extra[0].GetString().ShouldBe("spring-tenant-default");
    }

    [Fact]
    public async Task PullImageAsync_PostsImageAndTimeout()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHandler(async (req, _) =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var runtime = CreateRuntime(handler);
        await runtime.PullImageAsync(
            "ghcr.io/cvoya/claude:1.2.3",
            TimeSpan.FromSeconds(60),
            TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.Method.ShouldBe(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.ShouldBe("/v1/images/pull");
        var body = await captured.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("image").GetString().ShouldBe("ghcr.io/cvoya/claude:1.2.3");
        parsed.RootElement.GetProperty("timeoutSeconds").GetInt32().ShouldBe(60);
    }

    [Fact]
    public async Task PullImageAsync_504_ThrowsTimeoutException()
    {
        var handler = new FakeHandler(async (_, _) =>
            new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            {
                Content = new StringContent("registry slow"),
            });

        var runtime = CreateRuntime(handler);

        // PullImageActivity classifies on the exception type — the contract
        // here is "504 ↔ TimeoutException" so the worker's existing branch
        // for slow registries keeps working through the dispatcher hop.
        await Should.ThrowAsync<TimeoutException>(async () =>
            await runtime.PullImageAsync(
                "alpine:latest",
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PullImageAsync_502_ThrowsInvalidOperation()
    {
        var handler = new FakeHandler(async (_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("manifest unknown"),
            });

        var runtime = CreateRuntime(handler);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await runtime.PullImageAsync(
                "alpine:does-not-exist",
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateNetworkAsync_PostsName()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHandler(async (req, _) =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var runtime = CreateRuntime(handler);
        await runtime.CreateNetworkAsync("spring-net-abc", TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.Method.ShouldBe(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.ShouldBe("/v1/networks");
        var body = await captured.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("name").GetString().ShouldBe("spring-net-abc");
    }

    [Fact]
    public async Task RemoveNetworkAsync_IssuesDelete()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHandler(async (req, _) =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        var runtime = CreateRuntime(handler);
        await runtime.RemoveNetworkAsync("spring-net-abc", TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.Method.ShouldBe(HttpMethod.Delete);
        captured.RequestUri!.AbsolutePath.ShouldBe("/v1/networks/spring-net-abc");
    }

    [Fact]
    public async Task RemoveNetworkAsync_404IsTreatedAsNoOp()
    {
        var handler = new FakeHandler(async (_, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var runtime = CreateRuntime(handler);

        // Idempotent contract: removing a missing network must not throw,
        // so ContainerLifecycleManager's teardown sweep is safe after a
        // partial-failure boot.
        await runtime.RemoveNetworkAsync("missing", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ProbeContainerHttpAsync_PostsUrlAndParsesHealthy()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHandler(async (req, _) =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { healthy = true }),
            };
        });

        var runtime = CreateRuntime(handler);
        var healthy = await runtime.ProbeContainerHttpAsync(
            "sidecar-1",
            "http://localhost:3500/v1.0/healthz",
            TestContext.Current.CancellationToken);

        healthy.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured!.Method.ShouldBe(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.ShouldBe("/v1/containers/sidecar-1/probe");
        var body = await captured.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("url").GetString().ShouldBe("http://localhost:3500/v1.0/healthz");
    }

    [Fact]
    public async Task ProbeContainerHttpAsync_404IsTreatedAsUnhealthy()
    {
        var handler = new FakeHandler(async (_, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var runtime = CreateRuntime(handler);

        // The probe collapses every failure mode (missing container, missing
        // wget, non-2xx, DNS failure) to a single boolean so the polling
        // loop in DaprSidecarManager owns retry semantics. 404 specifically
        // is "container vanished" — we treat it as unhealthy rather than a
        // hard exception so a teardown race during sidecar boot doesn't
        // crash the worker.
        var healthy = await runtime.ProbeContainerHttpAsync(
            "missing-sidecar",
            "http://localhost:3500/v1.0/healthz",
            TestContext.Current.CancellationToken);

        healthy.ShouldBeFalse();
    }

    [Fact]
    public async Task SendHttpJsonAsync_PostsBase64Body_AndDecodesResponse()
    {
        // ADR 0028 / #1160: this is the worker side of the dispatcher-proxied
        // A2A message-send. The wire shape is base64 in / base64 out so the
        // dispatcher service can hand the body straight to `wget --post-file`
        // without re-parsing it. Make sure the client serialises and decodes
        // the contract the dispatcher expects.
        HttpRequestMessage? captured = null;
        var responseBody = "{\"jsonrpc\":\"2.0\",\"result\":{}}"u8.ToArray();
        var handler = new FakeHandler(async (req, _) =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    statusCode = 200,
                    bodyBase64 = Convert.ToBase64String(responseBody),
                }),
            };
        });

        var runtime = CreateRuntime(handler);
        var requestBody = "{\"jsonrpc\":\"2.0\",\"method\":\"message/send\"}"u8.ToArray();
        var result = await runtime.SendHttpJsonAsync(
            "agent-container-1",
            "http://localhost:8999/",
            requestBody,
            TestContext.Current.CancellationToken);

        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe(responseBody);

        captured.ShouldNotBeNull();
        captured!.Method.ShouldBe(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.ShouldBe("/v1/containers/agent-container-1/a2a");
        var body = await captured.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var parsed = JsonDocument.Parse(body);
        parsed.RootElement.GetProperty("url").GetString().ShouldBe("http://localhost:8999/");
        var sent = Convert.FromBase64String(parsed.RootElement.GetProperty("bodyBase64").GetString()!);
        sent.ShouldBe(requestBody);
    }

    [Fact]
    public async Task SendHttpJsonAsync_404IsTreatedAsBadGateway()
    {
        // The dispatcher returns 404 when the container is gone (race with a
        // teardown). The client collapses that to the same 502 the
        // dispatcher would have returned for a wget non-zero exit, so the
        // A2A SDK consumer sees one consistent failure mode regardless of
        // which side observed the death first. Mirrors the equivalent
        // contract for ProbeContainerHttpAsync.
        var handler = new FakeHandler(async (_, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var runtime = CreateRuntime(handler);
        var result = await runtime.SendHttpJsonAsync(
            "missing-container",
            "http://localhost:8999/",
            [1, 2, 3],
            TestContext.Current.CancellationToken);

        result.StatusCode.ShouldBe(502);
        result.Body.ShouldBeEmpty();
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

    // ── WaitForExitAsync tests (#2198) ───────────────────────────────────────

    [Fact]
    public async Task WaitForExitAsync_LongPollsAndReturnsCapturedResult()
    {
        // The worker forwards wait-for-exit to POST /v1/containers/{id}/wait-for-exit;
        // the dispatcher long-holds until `podman wait` returns, then echoes the
        // exit code + captured stdout/stderr. Added in #2198 to decompose Run
        // into Start + (probe daprd via exec) + Wait.
        HttpRequestMessage? captured = null;
        var handler = new FakeHandler(async (req, _) =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    id = "app-container-1",
                    exitCode = 7,
                    stdout = "hello",
                    stderr = "warning",
                }),
            };
        });

        var runtime = CreateRuntime(handler);
        var result = await runtime.WaitForExitAsync(
            "app-container-1", TestContext.Current.CancellationToken);

        result.ContainerId.ShouldBe("app-container-1");
        result.ExitCode.ShouldBe(7);
        result.StandardOutput.ShouldBe("hello");
        result.StandardError.ShouldBe("warning");

        captured.ShouldNotBeNull();
        captured!.Method.ShouldBe(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.ShouldBe("/v1/containers/app-container-1/wait-for-exit");
    }

    [Fact]
    public async Task WaitForExitAsync_404Throws()
    {
        // Container unknown → InvalidOperationException so the caller sees a
        // clean failure instead of waiting forever for a wait that can never
        // succeed.
        var handler = new FakeHandler(async (_, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var runtime = CreateRuntime(handler);
        await Should.ThrowAsync<InvalidOperationException>(() =>
            runtime.WaitForExitAsync("missing", TestContext.Current.CancellationToken));
    }

    // ── GetHealthAsync retry-policy tests (#1379) ────────────────────────────

    [Fact]
    public async Task GetHealthAsync_HealthyResponse_ReturnsHealthy()
    {
        // Baseline: a clean 200 with status="healthy" maps to ContainerHealth.Healthy == true.
        var handler = new FakeHandler(async (_, _) =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { status = "healthy", method = "HEALTHCHECK" }),
            });

        var runtime = CreateRuntime(handler);
        var result = await runtime.GetHealthAsync("container-1", TestContext.Current.CancellationToken);

        result.Healthy.ShouldBeTrue();
        result.Detail.ShouldBe("HEALTHCHECK");
    }

    [Fact]
    public async Task GetHealthAsync_503WithUnhealthyBody_ReturnsUnhealthyWithoutRetry()
    {
        // 503 with a valid body is the dispatcher's documented "unhealthy"
        // response — it is NOT a transient failure and must never be retried.
        var callCount = 0;
        var handler = new FakeHandler(async (_, _) =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = JsonContent.Create(new { status = "unhealthy", reason = "exit code 1" }),
            };
        });

        var runtime = CreateRuntime(handler);
        var result = await runtime.GetHealthAsync("container-2", TestContext.Current.CancellationToken);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldBe("exit code 1");
        // Exactly one HTTP call — the 503 was treated as a semantic response, not a transport failure.
        callCount.ShouldBe(1);
    }

    [Fact]
    public async Task GetHealthAsync_404_ThrowsWithoutRetry()
    {
        // 404 means the container is not known to the dispatcher. This is a
        // definitive answer — do NOT retry; that would just hammer the dispatcher
        // N times for a container it has never seen.
        var callCount = 0;
        var handler = new FakeHandler(async (_, _) =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var runtime = CreateRuntime(handler);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await runtime.GetHealthAsync("unknown-container", TestContext.Current.CancellationToken));

        callCount.ShouldBe(1, "404 is definitive — no retry");
    }

    [Fact]
    public async Task GetHealthAsync_TransientFailureThenSuccess_RetriesAndReturnsHealthy()
    {
        // A transient 500 on the first attempt is followed by a successful 200.
        // The retry policy must recover and return the healthy result.
        var callCount = 0;
        var handler = new FakeHandler(async (_, _) =>
        {
            callCount++;
            if (callCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("dispatcher restarting"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { status = "healthy", method = "HEALTHCHECK" }),
            };
        });

        var runtime = CreateRuntime(handler);
        var result = await runtime.GetHealthAsync("container-3", TestContext.Current.CancellationToken);

        result.Healthy.ShouldBeTrue();
        callCount.ShouldBe(2, "one transient failure followed by success = 2 total calls");
    }

    [Fact]
    public async Task GetHealthAsync_PersistentTransientFailures_ExhaustsRetriesAndThrows()
    {
        // All attempts return 500. After exhausting the retry budget (3 total
        // attempts) the method must throw, not loop forever.
        var callCount = 0;
        var handler = new FakeHandler(async (_, _) =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("permanent dispatcher error"),
            };
        });

        var runtime = CreateRuntime(handler);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await runtime.GetHealthAsync("container-4", TestContext.Current.CancellationToken));

        // 3 total attempts: initial + 2 retries (matching HealthRetryDelays.Length).
        callCount.ShouldBe(3, "3 total attempts before giving up");
    }

    [Fact]
    public async Task GetHealthAsync_NetworkExceptionThenSuccess_RetriesAndReturnsHealthy()
    {
        // A network-level exception (HttpRequestException) on the first call is
        // treated as transient. The second attempt succeeds.
        var callCount = 0;
        var handler = new FakeHandler((_, _) =>
        {
            callCount++;
            if (callCount == 1)
            {
                throw new HttpRequestException("connection refused");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { status = "healthy", method = "HEALTHCHECK" }),
            });
        });

        var runtime = CreateRuntime(handler);
        var result = await runtime.GetHealthAsync("container-5", TestContext.Current.CancellationToken);

        result.Healthy.ShouldBeTrue();
        callCount.ShouldBe(2, "one network failure followed by success = 2 total calls");
    }

    [Fact]
    public async Task GetHealthAsync_PersistentNetworkException_ExhaustsRetriesAndThrows()
    {
        // All attempts throw HttpRequestException. The method must propagate an
        // InvalidOperationException wrapping the last network error.
        var callCount = 0;
        var handler = new FakeHandler((_, _) =>
        {
            callCount++;
            throw new HttpRequestException($"connection refused (attempt {callCount})");
        });

        var runtime = CreateRuntime(handler);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await runtime.GetHealthAsync("container-6", TestContext.Current.CancellationToken));

        callCount.ShouldBe(3, "3 total attempts before giving up");
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
