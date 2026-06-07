// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Thrown by <see cref="IContainerRuntime.RunAsync"/> /
/// <see cref="IContainerRuntime.StartAsync"/> when a requested bind-mount
/// <em>source</em> path does not exist on the dispatcher host.
/// </summary>
/// <remarks>
/// <para>
/// Podman fails such a launch with <c>Error: statfs &lt;src&gt;: no such file
/// or directory</c> and exit code 125, producing <b>no</b> container — a
/// cryptic surface a caller only sees as "the agent runtime never came up"
/// (#3101). The most common trigger on a local single-host deploy is a stale
/// or placeholder delegated-agent Dapr components path: the per-dispatch daprd
/// sidecar bind-mounts <c>&lt;base&gt;/profiles/&lt;provider&gt;</c> and a
/// <c>Dapr__Sidecar__DelegatedSpringVoyageAgentComponentsPath</c> that points
/// at a non-existent directory makes that source vanish.
/// </para>
/// <para>
/// A missing bind-mount source is a <b>permanent</b> deployment/configuration
/// condition, not a transient "container not ready yet" — every retry fails
/// identically. <see cref="ProcessContainerRuntime"/> therefore validates the
/// sources before shelling out and fast-fails with this actionable, path-naming
/// exception instead of the raw <c>statfs</c> dump. The dispatcher HTTP surface
/// maps it to <c>422 Unprocessable Entity</c> so the worker (and the turn's
/// activity stream) sees why the launch was refused rather than a bodyless 500.
/// </para>
/// </remarks>
public sealed class BindMountSourceMissingException : SpringException
{
    /// <summary>Machine-readable issue code stamped on the exception.</summary>
    public const string Code = "BindMountSourceMissing";

    /// <summary>Issue source bucket: this is a deployment/configuration defect.</summary>
    public const string IssueSource = "configuration";

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="BindMountSourceMissingException"/> class.
    /// </summary>
    /// <param name="message">The actionable error message.</param>
    public BindMountSourceMissingException(string message)
        : base(message)
    {
        this.WithIssue(Code, IssueSource);
    }

    /// <summary>
    /// Builds an exception naming the missing host <paramref name="source"/>
    /// path and the full <paramref name="mount"/> spec it came from, with
    /// guidance toward the usual cause.
    /// </summary>
    /// <param name="source">The absolute host path the bind mount referenced.</param>
    /// <param name="mount">The full <c>source:target[:opts]</c> mount spec.</param>
    public static BindMountSourceMissingException ForMount(string source, string mount)
        => new(
            $"Bind-mount source '{source}' does not exist on the dispatcher host "
            + $"(from volume mount '{mount}'). The container runtime would fail this launch "
            + "with `statfs … no such file or directory` (exit 125) and start no container. "
            + "Ensure the host path exists before launch; a stale or placeholder "
            + "Dapr__Sidecar__DelegatedSpringVoyageAgentComponentsPath (mounted as "
            + "<base>/profiles/<provider>) is the usual cause. "
            + "See https://github.com/cvoya-com/spring-voyage/issues/3101.");
}
