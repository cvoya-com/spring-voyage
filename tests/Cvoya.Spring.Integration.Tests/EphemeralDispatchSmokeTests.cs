// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Opt-in integration smoke for the unified ephemeral A2A dispatch path
/// introduced in PR 5 of the #1087 series. The test runs a real container
/// via the host's container runtime (podman or docker, whichever
/// <see cref="ProcessContainerRuntime"/> resolves) and confirms that the
/// dispatcher's <c>StartAsync → readiness → SendA2A → StopAsync</c> flow
/// completes against a real workload.
///
/// The harness is deliberately small: it exercises only
/// <see cref="ProcessContainerRuntime.StartAsync"/> /
/// <see cref="ProcessContainerRuntime.StopAsync"/> against an
/// <c>alpine:latest</c> container running <c>sh -c "sleep 5; echo hello"</c>.
/// We don't stand up a real A2A bridge here — that's covered by the
/// dispatcher-smoke shell script in <c>deployment/scripts/</c> and by CI's
/// agent-image smoke. This test asserts the lifecycle plumbing the new
/// ephemeral path adds (detached start, registry tracking, registry-driven
/// teardown) works end-to-end without `sleep infinity` hanging the dispatch.
///
/// Skipped automatically when neither podman nor docker is on PATH. To run
/// locally: <c>SPRING_RUN_DOCKER_SMOKE=1 dotnet test ...</c> — without the
/// env var the test is also skipped to keep CI on environments without a
/// container runtime green.
/// </summary>
public class EphemeralDispatchSmokeTests
{
    [Fact]
    [Trait("Category", "RequiresDocker")]
    public async Task EphemeralRegistry_StartAndRelease_RoundTripsThroughContainerRuntime()
    {
        if (Environment.GetEnvironmentVariable("SPRING_RUN_DOCKER_SMOKE") != "1")
        {
            Assert.Skip("Set SPRING_RUN_DOCKER_SMOKE=1 to run this Docker-gated smoke locally.");
        }

        var binary = ResolveContainerBinary();
        if (binary is null)
        {
            Assert.Skip("Neither 'podman' nor 'docker' is on PATH; skipping ephemeral-dispatch smoke.");
        }

        var loggerFactory = NullLoggerFactory.Instance;
        var runtimeOptions = Options.Create(new ContainerRuntimeOptions());
        IContainerRuntime runtime = binary switch
        {
            "podman" => new PodmanRuntime(runtimeOptions, loggerFactory),
            "docker" => new DockerRuntime(runtimeOptions, loggerFactory),
            _ => throw new InvalidOperationException($"unexpected binary {binary}"),
        };
        var dapr = Substitute.For<IDaprSidecarManager>();
        var clm = new ContainerLifecycleManager(
            runtime, dapr, Options.Create(new DaprSidecarOptions()), loggerFactory);
        var volumeManager = new AgentVolumeManager(runtime, loggerFactory);
        var registry = new EphemeralAgentRegistry(runtime, clm, volumeManager, loggerFactory);

        // Pull alpine:latest first so StartAsync below doesn't race the
        // implicit pull. PullImageAsync is idempotent on cached images.
        await runtime.PullImageAsync("docker.io/library/alpine:latest", TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

        var config = new ContainerConfig(
            Image: "docker.io/library/alpine:latest",
            // Mirrors the symptom in #1087: the legacy dispatcher path would
            // run `sleep infinity` and hang. The unified ephemeral path
            // doesn't wait on stdout — it tears the container down via the
            // registry as soon as the turn drains.
            Command: ["sh", "-c", "echo hello && sleep 30"],
            ExtraHosts: ["host.docker.internal:host-gateway"]);

        var containerId = await runtime.StartAsync(config, TestContext.Current.CancellationToken);
        containerId.ShouldNotBeNullOrEmpty();

        var lease = registry.Register("smoke-agent", "smoke-conv", containerId);
        registry.GetAllEntries().ShouldContain(e => e.ContainerId == containerId);

        // Release should stop the container even though it's mid-`sleep 30`.
        // This is the behaviour that fixes #1087 — the dispatcher does not
        // wait for the agent process to exit on its own.
        var sw = Stopwatch.StartNew();
        await registry.ReleaseAsync(lease, TestContext.Current.CancellationToken);
        sw.Stop();

        registry.GetAllEntries().ShouldBeEmpty();
        // `docker stop` defaults to a 10s SIGTERM grace; alpine's `sh` exits
        // promptly on SIGTERM so the round-trip should be well under 5s.
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// End-to-end wire smoke for issue #1198: spin up the actual
    /// <c>deployment/agent-sidecar/</c> bridge as a Node subprocess on
    /// a free port, then call it with the dispatcher's real
    /// <see cref="A2A.V0_3.A2AClient"/> over A2A. Asserts that
    /// <c>SendMessageAsync</c> deserializes the bridge's v0.3 response
    /// without throwing — validating the kebab-case-enum and
    /// kind-discriminated-result contract end-to-end.
    ///
    /// Gated on <c>SPRING_RUN_DOCKER_SMOKE=1</c>. The bridge must be
    /// built first (<c>cd deployment/agent-sidecar &amp;&amp; npm install
    /// &amp;&amp; npm run build</c>); the test skips with a clear message
    /// if the built artifact isn't on disk.
    /// </summary>
    [Fact]
    [Trait("Category", "RequiresDocker")]
    public async Task BridgeRoundtrip_V0_3WireShape_DispatcherDeserializesWithoutJsonException()
    {
        if (Environment.GetEnvironmentVariable("SPRING_RUN_DOCKER_SMOKE") != "1")
        {
            Assert.Skip("Set SPRING_RUN_DOCKER_SMOKE=1 to run this bridge-roundtrip smoke locally.");
        }

        var repoRoot = ResolveRepoRoot();
        var bridgeDist = Path.Combine(repoRoot, "deployment", "agent-sidecar", "dist", "cli.js");
        if (!File.Exists(bridgeDist))
        {
            Assert.Skip(
                $"Bridge dist not found at '{bridgeDist}'. " +
                "Run 'npm install && npm run build' in deployment/agent-sidecar first.");
        }

        var port = FindFreeTcpPort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Spawn the bridge: SPRING_AGENT_ARGV points at node -e 'echo' so every
        // message/send produces stdout output mapped as a completed task.
        var echoArgv = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            "node",
            "-e",
            "let b='';process.stdin.on('data',c=>b+=c);process.stdin.on('end',()=>process.stdout.write('ack:'+b))",
        });

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(bridgeDist);
        psi.EnvironmentVariables["AGENT_PORT"] = port.ToString();
        psi.EnvironmentVariables["SPRING_AGENT_ARGV"] = echoArgv;

        using var bridge = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start bridge process.");

        using var probe = new HttpClient();
        var endpoint = new Uri($"http://127.0.0.1:{port}/");
        var ready = await WaitForBridgeReadyAsync(probe, endpoint, cts.Token);
        ready.ShouldBeTrue("Bridge did not become ready within 15 s.");

        // Use the A2A V0_3 client — same as the dispatcher uses in production.
        using var client = new HttpClient { BaseAddress = endpoint };
        var a2aClient = new A2A.V0_3.A2AClient(endpoint, client);

        var request = new A2A.V0_3.MessageSendParams
        {
            Message = new A2A.V0_3.AgentMessage
            {
                Role = A2A.V0_3.MessageRole.User,
                Parts = [new A2A.V0_3.TextPart { Text = "smoke-ping" }],
                MessageId = Guid.NewGuid().ToString(),
            },
        };

        // Should not throw a JsonException on deserialization.
        var response = await a2aClient.SendMessageAsync(request, cts.Token);

        response.ShouldBeOfType<A2A.V0_3.AgentTask>();
        var task = (A2A.V0_3.AgentTask)response;
        task.Status.State.ShouldBe(A2A.V0_3.TaskState.Completed);

        bridge.Kill();
        await bridge.WaitForExitAsync(CancellationToken.None);
    }

    private static async Task<bool> WaitForBridgeReadyAsync(HttpClient probe, Uri endpoint, CancellationToken cancellationToken)
    {
        var agentCardUri = new Uri(endpoint, "/.well-known/agent.json");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await probe.GetAsync(agentCardUri, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch
            {
                // not ready yet
            }
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
        return false;
    }

    private static int FindFreeTcpPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ResolveRepoRoot()
    {
        // The test binary lives under
        // tests/Cvoya.Spring.Integration.Tests/bin/Debug/netN.0/. Walk
        // up until we find the AGENTS.md marker (the repo root marker
        // for the rest of the tests).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not resolve repository root from AppContext.BaseDirectory.");
    }

    private static string? ResolveContainerBinary()
    {
        if (IsOnPath("podman")) return "podman";
        if (IsOnPath("docker")) return "docker";
        return null;
    }

    private static bool IsOnPath(string binary)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "where" : "which",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(binary);
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.WaitForExit(2000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}