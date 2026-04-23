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

using A2A;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
        var registry = new EphemeralAgentRegistry(runtime, loggerFactory);

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
    /// End-to-end wire smoke for issue #1115: spin up the actual
    /// <c>deployment/agent-sidecar/</c> bridge as a Node subprocess on
    /// a free port, then call it with the dispatcher's real
    /// <see cref="A2AClient"/> over A2A. Asserts that
    /// <c>SendMessageAsync</c> deserializes the bridge's response
    /// without throwing a <c>JsonException</c> on
    /// <c>$.task.status.state</c> — the regression that #1115 was
    /// filed for.
    ///
    /// Gated on the same <c>SPRING_RUN_DOCKER_SMOKE</c> env var as the
    /// rest of this file. The bridge must be built first
    /// (<c>cd deployment/agent-sidecar &amp;&amp; npm install &amp;&amp; npm run build</c>);
    /// the test skips with a clear message if the built artifact
    /// isn't on disk.
    /// </summary>
    [Fact]
    [Trait("Category", "RequiresDocker")]
    public async Task BridgeRoundtrip_ProtoStyleEnums_DispatcherDeserializesWithoutJsonException()
    {
        if (Environment.GetEnvironmentVariable("SPRING_RUN_DOCKER_SMOKE") != "1")
        {
            Assert.Skip("Set SPRING_RUN_DOCKER_SMOKE=1 to run this opt-in bridge smoke locally.");
        }

        if (!IsOnPath("node"))
        {
            Assert.Skip("`node` is not on PATH; skipping bridge wire smoke.");
        }

        var repoRoot = ResolveRepoRoot();
        var bridgeCli = Path.Combine(repoRoot, "deployment", "agent-sidecar", "dist", "cli.js");
        if (!File.Exists(bridgeCli))
        {
            Assert.Skip(
                $"Built bridge CLI not found at '{bridgeCli}'. Run " +
                "`(cd deployment/agent-sidecar && npm install && npm run build)` first.");
        }

        var port = FindFreeTcpPort();
        var psi = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(bridgeCli);
        psi.Environment["AGENT_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["AGENT_NAME"] = "wire-smoke-1115";
        // ["sh","-c","cat"] echoes whatever the dispatcher pipes to stdin
        // back through stdout — same trick tests/scripts/smoke-1087.sh
        // uses to keep the smoke hermetic (no Anthropic key, no model).
        psi.Environment["SPRING_AGENT_ARGV"] = "[\"sh\",\"-c\",\"cat\"]";

        using var bridge = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch bridge process.");

        try
        {
            var endpoint = new Uri($"http://127.0.0.1:{port}/");

            using var probeClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var ready = await WaitForBridgeReadyAsync(probeClient, endpoint, TestContext.Current.CancellationToken);
            ready.ShouldBeTrue("bridge did not bind /.well-known/agent.json within the readiness budget");

            using var httpClient = new HttpClient();
            var client = new A2AClient(endpoint, httpClient);

            // The actual regression this exercises: with the lowercase
            // A2A 0.3 spec form ("completed", "agent") the bridge used
            // to emit, this call throws JsonException at
            // $.task.status.state inside SendMessageAsync. With the
            // proto-style names the bridge emits today, it succeeds
            // and surfaces the artifact text.
            var request = new SendMessageRequest
            {
                Message = new A2A.Message
                {
                    Role = Role.User,
                    Parts = [new Part { Text = "ping-from-1115" }],
                    MessageId = Guid.NewGuid().ToString(),
                    ContextId = "smoke-ctx",
                },
                Configuration = new SendMessageConfiguration
                {
                    AcceptedOutputModes = ["text/plain"],
                },
            };

            var response = await client.SendMessageAsync(request, TestContext.Current.CancellationToken);

            response.PayloadCase.ShouldBe(SendMessageResponseCase.Task);
            response.Task.ShouldNotBeNull();
            response.Task!.Status.State.ShouldBe(TaskState.Completed);
            response.Task.Artifacts.ShouldNotBeNull();
            var text = response.Task.Artifacts!
                .SelectMany(a => a.Parts)
                .Select(p => p.Text)
                .FirstOrDefault(t => t is not null && t.Contains("ping-from-1115", StringComparison.Ordinal));
            text.ShouldNotBeNull("bridge should echo the prompt back through `cat`");
        }
        finally
        {
            try
            {
                if (!bridge.HasExited)
                {
                    bridge.Kill(entireProcessTree: true);
                    bridge.WaitForExit(2000);
                }
            }
            catch
            {
                // best-effort teardown; the test outcome already captured the failure if any.
            }
        }
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