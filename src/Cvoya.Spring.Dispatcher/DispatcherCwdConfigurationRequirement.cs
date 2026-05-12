// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

using Cvoya.Spring.Core.Configuration;

/// <summary>
/// Tier-1 requirement: the dispatcher process's current working directory
/// must be reachable. When the cwd's inode has been unlinked (typical
/// example: the dispatcher was launched from a git worktree under
/// <c>.claude/worktrees/&lt;N&gt;/</c> that was later removed), the process
/// keeps running and <c>/health</c> stays green, but every
/// <see cref="System.Diagnostics.Process.Start()"/> call fails with
/// <see cref="System.IO.FileNotFoundException"/> because posix_spawn on
/// macOS and Linux can't resolve the child's working directory. The
/// dispatcher then 500s on every <c>POST /v1/images/pull</c> and container
/// op with an opaque "Unable to find the specified file" message and no
/// hint that the cwd is the cause. See issue #1674.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mandatory.</b> A broken cwd makes the dispatcher strictly useless
/// (every shell-out fails) but invisible at the <c>/health</c> level — so
/// the tier-1 validator aborts boot instead of letting the operator
/// discover the problem through a storm of 500s.
/// </para>
/// <para>
/// <b>Why startup-only.</b> The cwd cannot change underneath a running
/// process in a way that's recoverable — once <c>getcwd()</c> returns
/// ENOENT, the only fix is to restart the process from a valid directory.
/// The validator is boot-time only (see
/// <see cref="IConfigurationRequirement"/>); a running dispatcher that
/// later loses its cwd will manifest through the error-translation path
/// in <c>ProcessContainerRuntime</c>.
/// </para>
/// </remarks>
public sealed class DispatcherCwdConfigurationRequirement(IDispatcherCwdProbe probe) : IConfigurationRequirement
{
    private readonly IDispatcherCwdProbe _probe =
        probe ?? throw new ArgumentNullException(nameof(probe));

    /// <inheritdoc />
    public string RequirementId => "dispatcher-cwd";

    /// <inheritdoc />
    public string DisplayName => "Dispatcher working directory is reachable";

    /// <inheritdoc />
    public string SubsystemName => "Dispatcher";

    /// <inheritdoc />
    public bool IsMandatory => true;

    /// <inheritdoc />
    public IReadOnlyList<string> EnvironmentVariableNames { get; } = Array.Empty<string>();

    /// <inheritdoc />
    public string? ConfigurationSectionPath => null;

    /// <inheritdoc />
    public string Description =>
        "The dispatcher process's current working directory must be reachable. "
        + "If the cwd's inode has been removed (common after deleting the git worktree the dispatcher was launched from), "
        + "Process.Start fails with FileNotFoundException on every shell-out and the dispatcher silently rejects every container op.";

    /// <inheritdoc />
    public Uri? DocumentationUrl { get; } =
        new Uri("https://github.com/cvoya-com/spring-voyage/issues/1674", UriKind.Absolute);

    /// <inheritdoc />
    public Task<ConfigurationRequirementStatus> ValidateAsync(CancellationToken cancellationToken)
    {
        var result = _probe.Probe();
        if (result.Ok)
        {
            return Task.FromResult(ConfigurationRequirementStatus.Met());
        }

        var cwdDescription = string.IsNullOrEmpty(result.Cwd)
            ? "unknown"
            : $"'{result.Cwd}'";
        var reason =
            $"Dispatcher working directory ({cwdDescription}) is unreachable: {result.Error}. "
            + "Every shell-out to the container runtime will fail with FileNotFoundException.";
        var suggestion =
            "Restart the dispatcher from a valid directory "
            + "(e.g. `./devops/deploy/spring-voyage-host.sh restart`). "
            + "See https://github.com/cvoya-com/spring-voyage/issues/1674.";
        return Task.FromResult(ConfigurationRequirementStatus.Invalid(
            reason,
            suggestion,
            new InvalidOperationException(reason + " " + suggestion)));
    }
}

/// <summary>
/// Abstracts "is the dispatcher's cwd reachable?" so
/// <see cref="DispatcherCwdConfigurationRequirement"/> is unit-testable
/// without mutating the test process's working directory (which would race
/// every parallel test in the same assembly).
/// </summary>
public interface IDispatcherCwdProbe
{
    /// <summary>
    /// Returns whether the probe can resolve the current working directory
    /// and stat its inode. Never throws; <see cref="DispatcherCwdProbeResult.Error"/>
    /// captures the failure reason when <see cref="DispatcherCwdProbeResult.Ok"/>
    /// is <c>false</c>.
    /// </summary>
    DispatcherCwdProbeResult Probe();
}

/// <summary>
/// Immutable result of a single <see cref="IDispatcherCwdProbe.Probe"/>
/// call.
/// </summary>
/// <param name="Ok">
/// <c>true</c> when the cwd resolved and its inode exists; <c>false</c>
/// when <see cref="Directory.GetCurrentDirectory"/> threw or the resolved
/// path no longer exists on disk.
/// </param>
/// <param name="Cwd">
/// The resolved working-directory path when <see cref="Directory.GetCurrentDirectory"/>
/// returned successfully. May be populated on a <c>false</c> result when
/// the syscall succeeded but the inode has been unlinked.
/// </param>
/// <param name="Error">
/// Human-readable failure reason. <c>null</c> on success.
/// </param>
public readonly record struct DispatcherCwdProbeResult(bool Ok, string? Cwd, string? Error);

/// <summary>
/// Default <see cref="IDispatcherCwdProbe"/> backed by
/// <see cref="Directory.GetCurrentDirectory"/> plus an existence check on
/// the returned path. Covers both known failure shapes:
/// <list type="bullet">
///   <item><c>getcwd()</c> throws (Linux, most macOS versions) when the cwd inode has been unlinked.</item>
///   <item><c>getcwd()</c> returns a stale cached path whose inode is gone (older macOS / exotic fs).</item>
/// </list>
/// </summary>
public sealed class DispatcherCwdProbe : IDispatcherCwdProbe
{
    /// <inheritdoc />
    public DispatcherCwdProbeResult Probe()
    {
        string cwd;
        try
        {
            cwd = Directory.GetCurrentDirectory();
        }
        catch (Exception ex)
        {
            return new DispatcherCwdProbeResult(
                Ok: false,
                Cwd: null,
                Error: $"Directory.GetCurrentDirectory() threw {ex.GetType().Name}: {ex.Message}");
        }

        if (!Directory.Exists(cwd))
        {
            return new DispatcherCwdProbeResult(
                Ok: false,
                Cwd: cwd,
                Error: "cwd path no longer exists on disk (inode unlinked?)");
        }

        return new DispatcherCwdProbeResult(Ok: true, Cwd: cwd, Error: null);
    }
}
