// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Tests;

using System;
using System.IO;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Configuration;

using Shouldly;

using Xunit;

/// <summary>
/// Pins the fail-fast contract for the dispatcher's cwd probe (#1674).
/// A dispatcher whose working directory has been unlinked keeps passing
/// <c>/health</c> but rejects every shell-out with an opaque
/// <see cref="FileNotFoundException"/>; the requirement aborts boot before
/// that happens.
/// </summary>
public class DispatcherCwdConfigurationRequirementTests
{
    [Fact]
    public async Task ValidateAsync_CwdReachable_ReturnsMet()
    {
        var probe = new FakeProbe(() => new DispatcherCwdProbeResult(Ok: true, Cwd: "/workspaces/spring", Error: null));
        var requirement = new DispatcherCwdConfigurationRequirement(probe);

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Met);
        status.FatalError.ShouldBeNull();
    }

    [Fact]
    public async Task ValidateAsync_CwdUnreachable_ReturnsInvalidWithFatalError()
    {
        var probe = new FakeProbe(() => new DispatcherCwdProbeResult(
            Ok: false,
            Cwd: "/tmp/deleted-worktree",
            Error: "cwd path no longer exists on disk (inode unlinked?)"));
        var requirement = new DispatcherCwdConfigurationRequirement(probe);

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.Severity.ShouldBe(SeverityLevel.Error);
        status.Reason.ShouldNotBeNull();
        // Reason must name the cwd path and the underlying error so a log
        // reader who sees only the FatalError message can diagnose.
        status.Reason.ShouldContain("/tmp/deleted-worktree");
        status.Reason.ShouldContain("inode unlinked");
        status.Suggestion.ShouldNotBeNull();
        status.Suggestion.ShouldContain("spring-voyage-host.sh restart");
        status.FatalError.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task ValidateAsync_CwdSyscallThrew_NarratesSymbolicCwd()
    {
        // When getcwd() itself fails, the probe cannot report a path —
        // the requirement must still render a coherent narration instead of
        // emitting an ugly "''" in the log.
        var probe = new FakeProbe(() => new DispatcherCwdProbeResult(
            Ok: false,
            Cwd: null,
            Error: "Directory.GetCurrentDirectory() threw IOException: No such file or directory"));
        var requirement = new DispatcherCwdConfigurationRequirement(probe);

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.Reason.ShouldNotBeNull();
        status.Reason.ShouldContain("unknown");
        status.Reason.ShouldContain("IOException");
    }

    [Fact]
    public void RequirementMetadata_IsStableAndMandatory()
    {
        var requirement = new DispatcherCwdConfigurationRequirement(
            new FakeProbe(() => new DispatcherCwdProbeResult(Ok: true, Cwd: "/", Error: null)));

        requirement.RequirementId.ShouldBe("dispatcher-cwd");
        requirement.SubsystemName.ShouldBe("Dispatcher");
        requirement.IsMandatory.ShouldBeTrue();
        requirement.ConfigurationSectionPath.ShouldBeNull();
        requirement.EnvironmentVariableNames.ShouldBeEmpty();
        requirement.DocumentationUrl.ShouldNotBeNull();
        requirement.DocumentationUrl!.ToString().ShouldContain("1674");
    }

    [Fact]
    public void DispatcherCwdProbe_RealCwd_ReturnsOk()
    {
        // The test host itself always has a reachable cwd (xUnit would have
        // failed to start otherwise), so the default probe must succeed.
        var probe = new DispatcherCwdProbe();

        var result = probe.Probe();

        result.Ok.ShouldBeTrue();
        result.Error.ShouldBeNull();
        result.Cwd.ShouldNotBeNullOrEmpty();
        Directory.Exists(result.Cwd).ShouldBeTrue();
    }

    private sealed class FakeProbe : IDispatcherCwdProbe
    {
        private readonly Func<DispatcherCwdProbeResult> _result;

        public FakeProbe(Func<DispatcherCwdProbeResult> result)
        {
            _result = result;
        }

        public DispatcherCwdProbeResult Probe() => _result();
    }
}
