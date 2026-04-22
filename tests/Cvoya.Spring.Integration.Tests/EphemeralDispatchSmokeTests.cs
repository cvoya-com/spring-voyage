// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

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