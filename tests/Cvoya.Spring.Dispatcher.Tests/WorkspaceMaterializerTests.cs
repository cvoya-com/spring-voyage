// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
            var sut = new WorkspaceMaterializer(options, NullLoggerFactory.Instance);

            var request = new WorkspaceRequest
            {
                MountPath = "/workspace",
                Files = new Dictionary<string, string>
                {
                    ["CLAUDE.md"] = "# system prompt",
                    [Path.Combine("subdir", "tool.json")] = "{}",
                },
            };

            var materialised = await sut.MaterializeAsync(request, TestContext.Current.CancellationToken);

            try
            {
                AssertHasFlag(File.GetUnixFileMode(materialised.HostDirectory), UnixFileMode.OtherRead);
                AssertHasFlag(File.GetUnixFileMode(materialised.HostDirectory), UnixFileMode.OtherExecute);

                AssertHasFlag(File.GetUnixFileMode(Path.Combine(materialised.HostDirectory, "CLAUDE.md")), UnixFileMode.OtherRead);

                var nestedDir = Path.Combine(materialised.HostDirectory, "subdir");
                AssertHasFlag(File.GetUnixFileMode(nestedDir), UnixFileMode.OtherRead);
                AssertHasFlag(File.GetUnixFileMode(nestedDir), UnixFileMode.OtherExecute);

                AssertHasFlag(File.GetUnixFileMode(Path.Combine(nestedDir, "tool.json")), UnixFileMode.OtherRead);
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