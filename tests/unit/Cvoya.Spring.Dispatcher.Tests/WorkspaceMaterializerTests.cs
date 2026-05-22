// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// xUnit collection scope used to opt the umask-mutating test out of the
/// default parallel runner. <c>libc::umask(2)</c> is process-wide; running it
/// concurrently with other tests in the same assembly would race and could
/// corrupt their file modes.
/// </summary>
[CollectionDefinition(nameof(WorkspaceMaterializerTestsCollection), DisableParallelization = true)]
public class WorkspaceMaterializerTestsCollection { }

/// <summary>
/// Pins the workspace permission contract that an e2e regression revealed:
/// the dispatcher process (running as the host user, e.g. uid 501 on macOS)
/// materialises a per-invocation directory that is later bind-mounted into a
/// container running as a *different* uid (the agent user inside the image,
/// typically uid 1000). Without an explicit chmod, the dispatcher's
/// inherited umask wins — and one shipped launcher used to leak
/// <c>umask 077</c>, producing 0700 dirs that the in-container agent could
/// not enter. Result: the launched agent never read <c>CLAUDE.md</c> /
/// <c>.mcp.json</c> and the entire dispatch turned into a silent no-op.
/// </summary>
[Collection(nameof(WorkspaceMaterializerTestsCollection))]
public class WorkspaceMaterializerTests
{
    [Fact]
    public async Task MaterializeAsync_DirectoryAndFiles_AreReadableByOtherUsers()
    {
        // Skip on Windows: the dispatcher does not run on Windows in any
        // shipped deployment, and File.SetUnixFileMode no-ops there.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "spring-ws-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var previousUmask = -1;
        try
        {
            // Force a hostile umask so we prove the materializer's chmod
            // (not the test runner's umask) is what makes the workspace
            // accessible. 0077 is the same umask the spring-voyage-host.sh
            // bug used to leak.
            // 0o077 expressed in decimal — C# has no octal literal syntax.
            // 077_8 == 63_10 == owner-only files / dirs after umask masking.
            previousUmask = TrySetProcessUmask(63);

            var options = Options.Create(new DispatcherOptions
            {
                WorkspaceRoot = root,
            });
            // No volume mounts → the bind-mount fallback path under test here.
            var volumeLocator = Substitute.For<IWorkspaceVolumeLocator>();
            var sut = new WorkspaceMaterializer(options, volumeLocator, NullLoggerFactory.Instance);

            var request = new WorkspaceRequest
            {
                MountPath = "/workspace",
                Files = new Dictionary<string, string>
                {
                    ["CLAUDE.md"] = "# system prompt",
                    [Path.Combine("subdir", "tool.json")] = "{}",
                },
            };

            var materialised = await sut.MaterializeAsync(
                request, requestedVolumeMounts: null, TestContext.Current.CancellationToken);

            try
            {
                AssertHasFlag(File.GetUnixFileMode(materialised.HostDirectory), UnixFileMode.OtherRead);
                AssertHasFlag(File.GetUnixFileMode(materialised.HostDirectory), UnixFileMode.OtherExecute);

                var claudeMd = Path.Combine(materialised.HostDirectory, "CLAUDE.md");
                AssertHasFlag(File.GetUnixFileMode(claudeMd), UnixFileMode.OtherRead);
                // World-writable: the in-container agent runs as a different
                // uid and the sidecar rewrites .mcp.json in place per turn
                // (#2580). A non-writable file fails that refresh with EACCES.
                AssertHasFlag(File.GetUnixFileMode(claudeMd), UnixFileMode.OtherWrite);

                var nestedDir = Path.Combine(materialised.HostDirectory, "subdir");
                AssertHasFlag(File.GetUnixFileMode(nestedDir), UnixFileMode.OtherRead);
                AssertHasFlag(File.GetUnixFileMode(nestedDir), UnixFileMode.OtherExecute);

                var nestedFile = Path.Combine(nestedDir, "tool.json");
                AssertHasFlag(File.GetUnixFileMode(nestedFile), UnixFileMode.OtherRead);
                AssertHasFlag(File.GetUnixFileMode(nestedFile), UnixFileMode.OtherWrite);
            }
            finally
            {
                sut.Cleanup(materialised);
            }
        }
        finally
        {
            if (previousUmask >= 0)
            {
                _ = TrySetProcessUmask(previousUmask);
            }
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    /// <summary>
    /// #2608: when the workspace mount path coincides with a named-volume
    /// mount the caller already requested, the launcher files are written
    /// straight into that volume's host mount point — no separate
    /// per-invocation bind mount, and <see cref="MaterializedWorkspace.MountSpec"/>
    /// is <c>null</c> so the caller appends nothing to the container's mounts.
    /// </summary>
    [Fact]
    public async Task MaterializeAsync_WritesIntoNamedVolume_WhenMountPathCoincides()
    {
        var volumeMountpoint = Path.Combine(
            Path.GetTempPath(), "spring-vol-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(volumeMountpoint);

        try
        {
            const string VolumeName = "spring-ws-agent-007";
            var volumeLocator = Substitute.For<IWorkspaceVolumeLocator>();
            volumeLocator
                .ResolveVolumeMountpointAsync(VolumeName, Arg.Any<CancellationToken>())
                .Returns(volumeMountpoint);

            // WorkspaceRoot is irrelevant on this path — the files go into the
            // volume, not a per-invocation directory.
            var options = Options.Create(new DispatcherOptions { WorkspaceRoot = "/nonexistent" });
            var sut = new WorkspaceMaterializer(options, volumeLocator, NullLoggerFactory.Instance);

            var request = new WorkspaceRequest
            {
                // Trailing slash on the request; the volume mount below has no
                // trailing slash — the materializer normalises both.
                MountPath = "/spring/workspace/",
                Files = new Dictionary<string, string>
                {
                    ["CLAUDE.md"] = "# system prompt",
                    [".mcp.json"] = "{}",
                },
            };

            var materialised = await sut.MaterializeAsync(
                request,
                requestedVolumeMounts: [$"{VolumeName}:/spring/workspace"],
                TestContext.Current.CancellationToken);

            // Files landed inside the volume's host mount point.
            materialised.HostDirectory.ShouldBe(volumeMountpoint);
            File.ReadAllText(Path.Combine(volumeMountpoint, "CLAUDE.md")).ShouldBe("# system prompt");
            File.ReadAllText(Path.Combine(volumeMountpoint, ".mcp.json")).ShouldBe("{}");

            // No bind-mount spec to append — the caller already mounts the volume.
            materialised.MountSpec.ShouldBeNull();

            // Cleanup is a no-op for a volume-backed workspace: the per-agent
            // persistent volume survives container exit.
            sut.Cleanup(materialised);
            Directory.Exists(volumeMountpoint).ShouldBeTrue();
            File.Exists(Path.Combine(volumeMountpoint, "CLAUDE.md")).ShouldBeTrue();
        }
        finally
        {
            try
            {
                if (Directory.Exists(volumeMountpoint))
                {
                    Directory.Delete(volumeMountpoint, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    /// <summary>
    /// #2608: when a volume mount is requested but its host mount point does
    /// not resolve (e.g. the volume was never created), the materializer falls
    /// back to the per-invocation bind mount so the launch still gets a
    /// populated workspace rather than silently launching an empty one.
    /// </summary>
    [Fact]
    public async Task MaterializeAsync_FallsBackToBindMount_WhenVolumeMountpointUnresolved()
    {
        var root = Path.Combine(Path.GetTempPath(), "spring-ws-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var volumeLocator = Substitute.For<IWorkspaceVolumeLocator>();
            volumeLocator
                .ResolveVolumeMountpointAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((string?)null);

            var options = Options.Create(new DispatcherOptions { WorkspaceRoot = root });
            var sut = new WorkspaceMaterializer(options, volumeLocator, NullLoggerFactory.Instance);

            var request = new WorkspaceRequest
            {
                MountPath = "/spring/workspace/",
                Files = new Dictionary<string, string> { ["CLAUDE.md"] = "# prompt" },
            };

            var materialised = await sut.MaterializeAsync(
                request,
                requestedVolumeMounts: ["spring-ws-agent-008:/spring/workspace"],
                TestContext.Current.CancellationToken);

            // Bind-mount fallback: a per-invocation directory under the root,
            // with a non-null MountSpec the caller must append.
            materialised.HostDirectory.ShouldStartWith(root);
            materialised.MountSpec.ShouldBe($"{materialised.HostDirectory}:/spring/workspace/");
            File.ReadAllText(Path.Combine(materialised.HostDirectory, "CLAUDE.md")).ShouldBe("# prompt");

            sut.Cleanup(materialised);
            Directory.Exists(materialised.HostDirectory).ShouldBeFalse();
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    /// <summary>
    /// A host bind-mount entry (<c>/host/dir:/spring/workspace</c>) is not a
    /// named volume — the materializer must not mistake it for one and must
    /// fall back to its own per-invocation bind mount.
    /// </summary>
    [Fact]
    public async Task MaterializeAsync_IgnoresHostBindMounts_WhenSelectingVolume()
    {
        var root = Path.Combine(Path.GetTempPath(), "spring-ws-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var volumeLocator = Substitute.For<IWorkspaceVolumeLocator>();
            var options = Options.Create(new DispatcherOptions { WorkspaceRoot = root });
            var sut = new WorkspaceMaterializer(options, volumeLocator, NullLoggerFactory.Instance);

            var request = new WorkspaceRequest
            {
                MountPath = "/spring/workspace/",
                Files = new Dictionary<string, string> { ["CLAUDE.md"] = "# prompt" },
            };

            var materialised = await sut.MaterializeAsync(
                request,
                // Source starts with '/': a host bind mount, not a named volume.
                requestedVolumeMounts: ["/host/dir:/spring/workspace"],
                TestContext.Current.CancellationToken);

            materialised.MountSpec.ShouldNotBeNull();
            materialised.HostDirectory.ShouldStartWith(root);

            // The volume locator must never be consulted — nothing was a volume.
            await volumeLocator.DidNotReceive()
                .ResolveVolumeMountpointAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

            sut.Cleanup(materialised);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    /// <summary>
    /// .NET does not expose umask in BCL. P/Invoke libc's <c>umask(2)</c> on
    /// Linux/macOS so we can prove the materializer wins regardless of the
    /// caller's umask. Returns the previous umask, or -1 if the call failed
    /// (in which case the test still runs against the inherited umask, which
    /// in CI is typically 0022 — not the hostile case but still useful).
    /// </summary>
    private static int TrySetProcessUmask(int newMask)
    {
        try
        {
            return LibC.umask(newMask);
        }
        catch (DllNotFoundException)
        {
            return -1;
        }
        catch (EntryPointNotFoundException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Shouldly's <c>ShouldHaveFlag</c> doesn't accept the <see cref="UnixFileMode"/>
    /// flags-enum on this xUnit/Shouldly combo, so we hand-roll the assertion.
    /// </summary>
    private static void AssertHasFlag(UnixFileMode actual, UnixFileMode flag)
    {
        // .NET's standard format strings have no octal specifier; format
        // by hand so the failure message reads as a familiar mode literal
        // (e.g. "octal=755") rather than the flags-enum word salad.
        ((actual & flag) == flag).ShouldBeTrue(
            $"expected mode {actual} to include {flag} (octal={Convert.ToString((int)actual, 8)})");
    }

    private static class LibC
    {
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "umask", SetLastError = true)]
        public static extern int umask(int mask);
    }
}
