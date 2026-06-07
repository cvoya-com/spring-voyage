// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Thrown by <see cref="IContainerRuntime.ProbeContainerHttpAsync"/> when the
/// in-container HTTP probe cannot run because the probe binary
/// (<c>curl</c>) is absent from the workload image's <c>PATH</c>.
/// </summary>
/// <remarks>
/// <para>
/// A missing probe binary is a <b>permanent</b> condition, not a transient
/// "endpoint not ready yet" — every subsequent probe attempt would fail the
/// same way. The readiness wait therefore fast-fails on this exception with a
/// specific, actionable message rather than burning the full readiness window
/// and reporting a generic timeout (#3085 gap 1).
/// </para>
/// <para>
/// The condition is only reachable on BYOI / native-A2A images
/// (<c>ai.runtime: a2a-process</c>) that ship their own base image — the
/// platform-built agent images (<c>spring-voyage-agent</c>, agent-base,
/// agent.dapr) install <c>curl</c> explicitly, so they never trigger it.
/// </para>
/// </remarks>
public sealed class ContainerProbeToolMissingException : SpringException
{
    /// <summary>Machine-readable issue code stamped on the exception.</summary>
    public const string Code = "ProbeToolMissing";

    /// <summary>Issue source bucket: this is a runtime / image defect.</summary>
    public const string IssueSource = "runtime";

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="ContainerProbeToolMissingException"/> class.
    /// </summary>
    /// <param name="message">The actionable error message.</param>
    public ContainerProbeToolMissingException(string message)
        : base(message)
    {
        this.WithIssue(Code, IssueSource);
    }

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="ContainerProbeToolMissingException"/> class with an inner
    /// exception carrying the underlying runtime failure.
    /// </summary>
    /// <param name="message">The actionable error message.</param>
    /// <param name="innerException">The underlying runtime failure.</param>
    public ContainerProbeToolMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
        this.WithIssue(Code, IssueSource);
    }

    /// <summary>
    /// Builds an exception with the canonical actionable message naming the
    /// offending image and the missing <c>curl</c> dependency.
    /// </summary>
    /// <param name="image">
    /// The workload image reference, when known (may be <c>null</c> on call
    /// paths that only have the container id).
    /// </param>
    /// <param name="stderr">The container runtime's <c>exec</c> stderr, surfaced verbatim for diagnosis.</param>
    public static ContainerProbeToolMissingException ForCurl(string? image, string? stderr)
    {
        var imageClause = string.IsNullOrWhiteSpace(image)
            ? "The image"
            : $"Image '{image}'";
        var stderrClause = string.IsNullOrWhiteSpace(stderr)
            ? string.Empty
            : $" Runtime exec error: {stderr.Trim()}";
        return new ContainerProbeToolMissingException(
            $"{imageClause} is missing `curl`, which the platform readiness probe "
            + "requires on the image's PATH to fetch `/.well-known/agent.json`. "
            + "Install curl in the image (BYOI / native-A2A images must ship it)."
            + stderrClause);
    }
}
