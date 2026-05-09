// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ProcessContainerRuntime"/> argv construction.
/// These tests verify the argv vector without launching actual containers.
/// </summary>
/// <remarks>
/// The previous implementation built a single space-joined string and
/// handed it to <c>ProcessStartInfo.Arguments</c>, which then re-split the
/// string on whitespace. Any env-var, label, or volume value that
/// contained a space (the assembled system prompt for a delegated
/// claude-code agent is the canonical offender — its first line is
/// <c>## Platform Instructions</c>) caused podman to interpret a stray
/// token as the image reference and exit 125 with
/// <c>parsing reference "Platform": repository name must be lowercase</c>.
/// The build helpers now return a typed argv vector so each value is one
/// argv entry and is passed verbatim to <c>posix_spawn</c> /
/// <c>CreateProcess</c>. Tests assert against that vector.
/// </remarks>
public class ProcessContainerRuntimeTests
{
    [Fact]
    public void BuildRunArguments_MinimalConfig_ProducesExpectedArgv()
    {
        var config = new ContainerConfig(Image: "my-image:latest");

        var args = ProcessContainerRuntime.BuildRunArguments(config, "spring-exec-test");

        args.ShouldBe(["run", "--rm", "--name", "spring-exec-test", "my-image:latest"]);
    }

    [Fact]
    public void BuildRunArguments_WithEnvironmentVariables_IncludesOneArgvEntryPerFlag()
    {
        var config = new ContainerConfig(
            Image: "my-image:latest",
            EnvironmentVariables: new Dictionary<string, string>
            {
                ["SPRING_SYSTEM_PROMPT"] = "hello",
                ["OTHER_VAR"] = "world",
            });

        var args = ProcessContainerRuntime.BuildRunArguments(config, "spring-exec-env");

        args.ShouldContain("-e");
        args.ShouldContain("SPRING_SYSTEM_PROMPT=hello");
        args.ShouldContain("OTHER_VAR=world");
        // The "-e" flag and its value are always adjacent argv entries so
        // the runtime CLI sees them as a single option pair.
        AssertFlagPair(args, "-e", "SPRING_SYSTEM_PROMPT=hello");
        AssertFlagPair(args, "-e", "OTHER_VAR=world");
    }

    [Fact]
    public void BuildRunArguments_EnvironmentValueWithWhitespace_IsSingleArgvEntry()
    {
        // Regression test for the production "parsing reference 'Platform':
        // repository name must be lowercase" exit-125 failure. The assembled
        // system prompt always opens with the literal `## Platform
        // Instructions` heading from PromptAssembler. Under the old
        // string-args path that header turned into separate argv tokens
        // (`-e SPRING_SYSTEM_PROMPT=##`, `Platform`, `Instructions`, ...) and
        // podman picked `Platform` as the image. Under ArgumentList the
        // value rides through as one argv entry no matter what whitespace,
        // newlines, quotes, or other shell-meaningful characters it carries.
        const string Prompt = "## Platform Instructions\nYou are an agent named \"test\" — be concise.";
        var config = new ContainerConfig(
            Image: "agent:v1",
            EnvironmentVariables: new Dictionary<string, string>
            {
                ["SPRING_SYSTEM_PROMPT"] = Prompt,
            });

        var args = ProcessContainerRuntime.BuildRunArguments(config, "spring-exec-prompt");

        args.ShouldContain($"SPRING_SYSTEM_PROMPT={Prompt}");
        AssertFlagPair(args, "-e", $"SPRING_SYSTEM_PROMPT={Prompt}");

        // The image is the only bare positional argv entry. None of the
        // prompt's tokens leak into the image position.
        var imageIndex = IndexOf(args, "agent:v1");
        imageIndex.ShouldBeGreaterThan(0);
        args.ShouldNotContain("Platform");
        args.ShouldNotContain("Instructions");
    }

    [Fact]
    public void BuildRunArguments_LabelValueWithWhitespace_IsSingleArgvEntry()
    {
        var config = new ContainerConfig(
            Image: "my-image:latest",
            Labels: new Dictionary<string, string>
            {
                ["spring.unit.display-name"] = "My Test Unit",
            });

        var args = ProcessContainerRuntime.BuildRunArguments(config, "spring-exec-label-ws");

        args.ShouldContain("spring.unit.display-name=My Test Unit");
        AssertFlagPair(args, "--label", "spring.unit.display-name=My Test Unit");
    }

    [Fact]
    public void BuildRunArguments_WithVolumeMounts_IncludesOneArgvEntryPerMount()
    {
        var config = new ContainerConfig(
            Image: "my-image:latest",
            VolumeMounts: ["/host/path:/container/path", "/data:/data:ro"]);

        var args = ProcessContainerRuntime.BuildRunArguments(config, "spring-exec-vol");

        AssertFlagPair(args, "-v", "/host/path:/container/path");
        AssertFlagPair(args, "-v", "/data:/data:ro");
    }

    [Fact]
    public void BuildRunArguments_WithNetworkName_IncludesNetworkFlagPair()
    {
        var config = new ContainerConfig(
            Image: "my-image:latest",
            NetworkName: "spring-net-abc");

        var args = ProcessContainerRuntime.BuildRunArguments(config, "spring-exec-net");

        AssertFlagPair(args, "--network", "spring-net-abc");
    }

    [Fact]
    public void BuildRunArguments_WithAdditionalNetworks_EmitsRepeatedNetworkFlags()
    {
        // ADR 0028 / issue #1166: workflow + unit containers dual-attach to
        // a per-tenant bridge alongside their per-workflow spring-net-<guid>
        // bridge. Both podman (>=4) and docker (>=20.10) accept --network
        // more than once at run time, attaching the container to every
        // named network.
        var config = new ContainerConfig(
            Image: "my-image:latest",
            NetworkName: "spring-net-abc",
            AdditionalNetworks: ["spring-tenant-default"]);

        var args = ProcessContainerRuntime.BuildRunArguments(config, "spring-exec-multi-net");

        AssertFlagPair(args, "--network", "spring-net-abc");
        AssertFlagPair(args, "--network", "spring-tenant-default");

        // Two separate flag pairs in the argv (no comma-joined form, no merged value).
        var networkFlagCount = args.Count(a => a == "--network");
        networkFlagCount.ShouldBe(2);
    }

    [Fact]
    public void BuildRunArguments_WithEmptyAdditionalNetworkEntry_SkipsIt()
    {
        // Defensive: a future caller building the list dynamically might
        // include a blank slot. Drop it rather than emit a malformed
        // `--network ""` pair the runtime would reject.
        var config = new ContainerConfig(
            Image: "my-image:latest",
            NetworkName: "spring-net-abc",
            AdditionalNetworks: ["", "spring-tenant-default", "  "]);

        var args = ProcessContainerRuntime.BuildRunArguments(config, "spring-exec-blank-net");

        var networkFlagCount = args.Count(a => a == "--network");
        networkFlagCount.ShouldBe(2);
        args.ShouldContain("spring-tenant-default");
    }

    [Fact]
    public void BuildRunArguments_WithExtraHosts_UsesCombinedAddHostForm()
    {
        var config = new ContainerConfig(
            Image: "my-image:latest",
            ExtraHosts: ["host.docker.internal:host-gateway"]);

        var args = ProcessContainerRuntime.BuildRunArguments(config, "spring-exec-hosts");

        // Podman / docker accept either `--add-host k:v` or
        // `--add-host=k:v`. We use the combined form because that's the
        // shape the previous string-builder produced (and what
        // A2AExecutionDispatcher expects on the wire). Pinning it here so a
        // future refactor doesn't silently switch shapes.
        args.ShouldContain("--add-host=host.docker.internal:host-gateway");
    }

    [Fact]
    public void BuildRunArguments_WithWorkingDirectory_IncludesWorkingDirFlagPair()
    {
        var config = new ContainerConfig(
            Image: "my-image:latest",
            WorkingDirectory: "/workspace");

        var args = ProcessContainerRuntime.BuildRunArguments(config, "spring-exec-wd");

        AssertFlagPair(args, "-w", "/workspace");
    }

    [Fact]
    public void BuildRunArguments_WithCommand_AppendsCommandTokensAfterImage()
    {
        // Command is now a list — each entry becomes one argv token verbatim,
        // no whitespace splitting. Producers that previously joined tokens
        // with single spaces (DaprSidecarManager, RunContainerProbeActivity)
        // were updated in #1093 to pass the list directly.
        var config = new ContainerConfig(
            Image: "agent:v1",
            Command: ["./daprd", "--app-id", "myapp", "--app-port", "8080"]);

        var args = ProcessContainerRuntime.BuildRunArguments(config, "spring-exec-cmd");

        var imageIndex = IndexOf(args, "agent:v1");
        imageIndex.ShouldBeGreaterThan(0);

        var commandTokens = args.Skip(imageIndex + 1).ToArray();
        commandTokens.ShouldBe(["./daprd", "--app-id", "myapp", "--app-port", "8080"]);
    }

    [Fact]
    public void BuildRunArguments_CommandTokenWithSpaces_IsForwardedAsSingleArgvEntry()
    {
        // Regression for #1063 / #1093: a single argv token that contains a
        // space must reach podman as one argument, not split on whitespace.
        var config = new ContainerConfig(
            Image: "agent:v1",
            Command: ["sh", "-c", "echo hello world"]);

        var args = ProcessContainerRuntime.BuildRunArguments(config, "spring-exec-spaces");

        var imageIndex = IndexOf(args, "agent:v1");
        var commandTokens = args.Skip(imageIndex + 1).ToArray();
        commandTokens.ShouldBe(["sh", "-c", "echo hello world"]);
    }

    [Fact]
    public void BuildRunArguments_FullConfig_HasCorrectFlagOrdering()
    {
        var config = new ContainerConfig(
            Image: "agent:v1",
            Command: ["run-agent"],
            EnvironmentVariables: new Dictionary<string, string> { ["KEY"] = "val" },
            VolumeMounts: ["/src:/app"],
            NetworkName: "my-net",
            Labels: new Dictionary<string, string> { ["app"] = "test" });

        var args = ProcessContainerRuntime.BuildRunArguments(config, "spring-exec-full");

        var networkIndex = IndexOf(args, "--network");
        var labelIndex = IndexOf(args, "--label");
        var envIndex = IndexOf(args, "-e");
        var volIndex = IndexOf(args, "-v");
        var imageIndex = IndexOf(args, "agent:v1");
        var commandIndex = IndexOf(args, "run-agent");

        networkIndex.ShouldBeLessThan(labelIndex);
        labelIndex.ShouldBeLessThan(envIndex);
        envIndex.ShouldBeLessThan(volIndex);
        volIndex.ShouldBeLessThan(imageIndex);
        imageIndex.ShouldBeLessThan(commandIndex);
    }

    [Fact]
    public void BuildStartArguments_ProducesDetachedFormWithSameFlags()
    {
        var config = new ContainerConfig(
            Image: "agent:v1",
            EnvironmentVariables: new Dictionary<string, string> { ["KEY"] = "val with spaces" });

        var args = ProcessContainerRuntime.BuildStartArguments(config, "spring-persistent-x");

        // `run -d` (detached) instead of `run --rm`; everything else
        // matches the run-builder shape.
        args[0].ShouldBe("run");
        args[1].ShouldBe("-d");
        args[2].ShouldBe("--name");
        args[3].ShouldBe("spring-persistent-x");
        AssertFlagPair(args, "-e", "KEY=val with spaces");
    }

    // ── BuildPullArguments / BuildImageInspectArguments tests (#1676 / #1698) ──

    [Fact]
    public void BuildPullArguments_IsBarePullWithImageAsTrailingArgv()
    {
        // The locally-cached short-circuit lives on the caller side as an
        // `image inspect` probe (see BuildImageInspectArguments below); the
        // pull itself must therefore stay as a vanilla `pull <image>` so
        // the dispatcher does not depend on the runtime's pull-policy
        // semantics. #1682 had wired `--policy missing` directly onto the
        // pull, which exits non-zero on podman 4.9.x once the image is
        // already in the store and propagates as a 502 from
        // POST /v1/images/pull (#1698 regression).
        var args = ProcessContainerRuntime.BuildPullArguments("ghcr.io/example/agent:latest");

        args.ShouldBe(["pull", "ghcr.io/example/agent:latest"]);
    }

    [Fact]
    public void BuildPullArguments_ImageRidesAsSingleTrailingArgvEntry()
    {
        // The image reference is always the trailing positional argv entry;
        // values containing characters podman would normally treat as
        // separators in a string-args path (`:`, `-`) must travel through
        // verbatim as one argv entry.
        var args = ProcessContainerRuntime.BuildPullArguments("registry.local:5000/team/img:tag-with-dashes");

        args.Count.ShouldBe(2);
        args[0].ShouldBe("pull");
        args[1].ShouldBe("registry.local:5000/team/img:tag-with-dashes");
    }

    [Fact]
    public void BuildImageInspectArguments_IsLocalStoreProbe()
    {
        // The pull short-circuit (#1676) relies on `image inspect` returning
        // exit 0 strictly when the image is in the local store. The argv
        // shape must stay as `image inspect <image>` so neither podman nor
        // docker is ever asked to round-trip to the registry from this path.
        var args = ProcessContainerRuntime.BuildImageInspectArguments("ghcr.io/example/agent:latest");

        args.ShouldBe(["image", "inspect", "ghcr.io/example/agent:latest"]);
    }

    [Fact]
    public void BuildImageInspectArguments_ImageRidesAsSingleTrailingArgvEntry()
    {
        var args = ProcessContainerRuntime.BuildImageInspectArguments("registry.local:5000/team/img:tag-with-dashes");

        args.Count.ShouldBe(3);
        args[0].ShouldBe("image");
        args[1].ShouldBe("inspect");
        args[2].ShouldBe("registry.local:5000/team/img:tag-with-dashes");
    }

    // ── RewriteUrlHost tests (ProbeHttpFromHostAsync helper, issue #1175) ──

    [Theory]
    [InlineData("http://localhost:8999/.well-known/agent.json", "172.17.0.5", "http://172.17.0.5:8999/.well-known/agent.json")]
    [InlineData("http://localhost:3500/v1.0/healthz", "10.89.0.12", "http://10.89.0.12:3500/v1.0/healthz")]
    [InlineData("http://127.0.0.1:8080/health", "192.168.100.2", "http://192.168.100.2:8080/health")]
    [InlineData("http://localhost:8999/", "fd00::1", "http://[fd00::1]:8999/")]
    public void RewriteUrlHost_ReplacesHostAndPreservesPortAndPath(
        string inputUrl, string newHost, string expectedUrl)
    {
        // ProbeHttpFromHostAsync converts in-container loopback URLs
        // (http://localhost:PORT/PATH) to host-routable URLs using the
        // container's bridge IP from `podman inspect`. The helper must
        // preserve the original port and path verbatim; only the host changes.
        var result = ProcessContainerRuntime.RewriteUrlHost(inputUrl, newHost);
        result.ShouldBe(expectedUrl);
    }

    [Fact]
    public void RewriteUrlHost_NoPort_PreservesSchemeDefaultPort()
    {
        // URLs without an explicit port (e.g. standard HTTP on 80) should
        // survive the rewrite without a spurious ":80" being appended.
        var result = ProcessContainerRuntime.RewriteUrlHost("http://localhost/health", "10.0.0.1");
        // UriBuilder keeps the default port implicit.
        result.ShouldBe("http://10.0.0.1/health");
    }

    // ── TranslateProcessStartFailure tests (#1674) ──
    //
    // The translator is unit-tested through its injectable-probe overload so
    // the tests never have to mutate the test runner's process cwd. Running
    // `Directory.SetCurrentDirectory(...)` inside a parallel xUnit collection
    // is a well-known landmine — sibling tests in other classes observe the
    // poisoned value and flake at random. The overload surface is visible
    // only to .Tests (via InternalsVisibleTo); production code uses the
    // default-probe entry point unchanged.

    [Fact]
    public void TranslateProcessStartFailure_HealthyCwd_ReturnsOriginalException()
    {
        // When the dispatcher's cwd is fine, the Process.Start failure is
        // honestly about the binary — we must not pile a misleading cwd
        // wrapper on top, or the operator will chase the wrong bug.
        var original = new System.IO.FileNotFoundException("Unable to find the specified file.");
        var healthyProbe = () => new ProcessContainerRuntime.CwdProbeResult(Cwd: "/workspaces/spring", Error: null);

        var translated = ProcessContainerRuntime.TranslateProcessStartFailure(
            original, "podman", healthyProbe);

        translated.ShouldBeSameAs(original);
    }

    [Fact]
    public void TranslateProcessStartFailure_UnreachableCwd_WrapsWithCwdNarration()
    {
        // The stat-branch shape: `getcwd()` returned a path but the inode
        // is gone (rm -rf of an ancestor, git worktree removal, etc.).
        var original = new System.IO.FileNotFoundException("Unable to find the specified file.");
        var unreachableProbe = () => new ProcessContainerRuntime.CwdProbeResult(
            Cwd: "/tmp/deleted-worktree/spring",
            Error: "cwd path no longer exists on disk (inode unlinked?)");

        var translated = ProcessContainerRuntime.TranslateProcessStartFailure(
            original, "podman", unreachableProbe);

        translated.ShouldNotBeSameAs(original);
        translated.ShouldBeOfType<InvalidOperationException>();
        translated.Message.ShouldContain("podman");
        translated.Message.ShouldContain("working directory");
        translated.Message.ShouldContain("/tmp/deleted-worktree/spring");
        translated.Message.ShouldContain("inode unlinked");
        translated.Message.ShouldContain("1674");
        translated.Message.ShouldContain("spring-voyage-host.sh restart");
        translated.InnerException.ShouldBeSameAs(original);
    }

    [Fact]
    public void TranslateProcessStartFailure_GetcwdThrew_NarratesSymbolicCwd()
    {
        // The syscall-branch shape: `getcwd()` itself failed, so the
        // probe has no path to quote. The translator must still render
        // something coherent — "''" in the log is useless.
        var original = new System.IO.FileNotFoundException("Unable to find the specified file.");
        var throwingProbe = () => new ProcessContainerRuntime.CwdProbeResult(
            Cwd: null,
            Error: "Directory.GetCurrentDirectory() threw IOException: No such file or directory");

        var translated = ProcessContainerRuntime.TranslateProcessStartFailure(
            original, "podman", throwingProbe);

        translated.ShouldBeOfType<InvalidOperationException>();
        translated.Message.ShouldContain("unknown");
        translated.Message.ShouldContain("IOException");
        translated.Message.ShouldContain("1674");
    }

    [Fact]
    public void TranslateProcessStartFailure_Win32Exception_RoutesThroughSameLogic()
    {
        // The .NET BCL sometimes surfaces posix_spawn failures as
        // Win32Exception (via System.ComponentModel) rather than
        // FileNotFoundException. The translator's exception-type filter in
        // StartProcessOrTranslate covers that; the translator itself is
        // type-agnostic, so pin the contract here.
        var original = new System.ComponentModel.Win32Exception(2, "No such file or directory");
        var unreachableProbe = () => new ProcessContainerRuntime.CwdProbeResult(
            Cwd: "/tmp/gone",
            Error: "cwd path no longer exists on disk (inode unlinked?)");

        var translated = ProcessContainerRuntime.TranslateProcessStartFailure(
            original, "podman", unreachableProbe);

        translated.ShouldBeOfType<InvalidOperationException>();
        translated.InnerException.ShouldBeSameAs(original);
        translated.Message.ShouldContain("/tmp/gone");
    }

    [Fact]
    public void DefaultCwdProbe_RealCwd_ReturnsNoError()
    {
        // The test host itself always has a reachable cwd (xUnit would not
        // have started otherwise), so the default probe must succeed and
        // never report an error back to the translator.
        var result = ProcessContainerRuntime.DefaultCwdProbe();

        result.Error.ShouldBeNull();
        result.Cwd.ShouldNotBeNullOrEmpty();
        Directory.Exists(result.Cwd).ShouldBeTrue();
    }

    /// <summary>
    /// Asserts that <paramref name="value"/> immediately follows
    /// <paramref name="flag"/> in <paramref name="args"/>. Used to pin the
    /// "flag and its value are adjacent argv entries" invariant — the same
    /// invariant that's required for the container CLI to read the value as
    /// the option's argument rather than a positional one.
    /// </summary>
    private static void AssertFlagPair(IReadOnlyList<string> args, string flag, string value)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == flag && args[i + 1] == value)
            {
                return;
            }
        }

        throw new Shouldly.ShouldAssertException(
            $"Expected argv to contain the adjacent pair [{flag}, {value}], but it did not. Argv was: [{string.Join(", ", args)}]");
    }

    private static int IndexOf(IReadOnlyList<string> args, string value)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}
