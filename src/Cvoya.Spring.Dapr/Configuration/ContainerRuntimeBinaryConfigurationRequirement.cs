// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Configuration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Options;

/// <summary>
/// Tier-1 requirement: the container runtime binary named by
/// <c>ContainerRuntime:RuntimeType</c> exists on <c>PATH</c> and is executable.
/// Registered on hosts that actually shell out to the binary (the dispatcher
/// host) so a misconfigured image fails boot rather than 500-ing on every
/// dispatch. See issue #984.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mandatory flag is <c>true</c>.</b> Unlike
/// <see cref="ContainerRuntimeConfigurationRequirement"/> — which only checks
/// that <c>RuntimeType</c> names a supported runtime and stays non-mandatory
/// so non-dispatcher hosts boot — this requirement is dispatcher-scoped and
/// fails startup when the binary is missing. The dispatcher is useless
/// without the binary, so crashing at boot is strictly better than ambient
/// 500s from <c>POST /v1/containers</c> the operator has to trace back.
/// </para>
/// <para>
/// <b>Probe shape.</b> Walks <c>PATH</c> directories via
/// <see cref="Environment.GetEnvironmentVariable(string)"/> and returns
/// <see cref="ConfigurationStatus.Met"/> on the first directory that contains
/// a matching executable. This is cheaper and more deterministic than
/// spawning the binary with <c>--version</c>, and it doesn't hang startup
/// behind a slow binary.
/// </para>
/// </remarks>
public sealed class ContainerRuntimeBinaryConfigurationRequirement : IConfigurationRequirement
{
    private readonly IOptions<ContainerRuntimeOptions> _optionsAccessor;
    private readonly IContainerRuntimeBinaryProbe _probe;

    /// <summary>
    /// Creates a new instance bound to the supplied options and probe.
    /// </summary>
    public ContainerRuntimeBinaryConfigurationRequirement(
        IOptions<ContainerRuntimeOptions> optionsAccessor,
        IContainerRuntimeBinaryProbe probe)
    {
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    /// <inheritdoc />
    public string RequirementId => "container-runtime-binary";

    /// <inheritdoc />
    public string DisplayName => "Container runtime binary on PATH";

    /// <inheritdoc />
    public string SubsystemName => "Container Runtime";

    /// <inheritdoc />
    public bool IsMandatory => true;

    /// <inheritdoc />
    public IReadOnlyList<string> EnvironmentVariableNames { get; } =
        new[] { "ContainerRuntime__RuntimeType", "PATH" };

    /// <inheritdoc />
    public string? ConfigurationSectionPath => "ContainerRuntime";

    /// <inheritdoc />
    public string Description =>
        "The runtime binary named by ContainerRuntime:RuntimeType ('podman' or 'docker') must exist on PATH on the dispatcher host. Without it every delegated-execution call crashes with 'No such file or directory'.";

    /// <inheritdoc />
    public Uri? DocumentationUrl { get; } =
        new Uri("https://github.com/cvoya-com/spring-voyage/blob/main/docs/architecture/deployment.md", UriKind.Absolute);

    /// <inheritdoc />
    public Task<ConfigurationRequirementStatus> ValidateAsync(CancellationToken cancellationToken)
    {
        var runtime = _optionsAccessor.Value.RuntimeType;

        if (string.IsNullOrWhiteSpace(runtime))
        {
            // The sibling ContainerRuntimeConfigurationRequirement already
            // reports an Invalid for an empty runtime type — no need to pile
            // a duplicate fatal error on top. Skip the probe.
            return Task.FromResult(ConfigurationRequirementStatus.Disabled(
                reason: "ContainerRuntime:RuntimeType is empty — binary probe skipped.",
                suggestion: "Set ContainerRuntime:RuntimeType to 'podman' or 'docker'."));
        }

        var binaryName = runtime.Trim().ToLowerInvariant();

        var timeout = TimeSpan.FromSeconds(2);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        string? resolvedPath;
        try
        {
            resolvedPath = _probe.TryResolveBinary(binaryName, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var reason = $"Container runtime binary probe for '{binaryName}' timed out after {timeout.TotalSeconds}s.";
            var suggestion = "Check that PATH is reachable and the filesystem is responsive.";
            return Task.FromResult(ConfigurationRequirementStatus.Invalid(
                reason,
                suggestion,
                new InvalidOperationException(reason + " " + suggestion)));
        }

        if (resolvedPath is null)
        {
            var reason =
                $"Container runtime binary '{binaryName}' was not found on PATH. " +
                "Every delegated-execution call will fail with 'No such file or directory'.";
            var suggestion =
                $"Install '{binaryName}' (or symlink the installed client — e.g. " +
                "`ln -sf /usr/bin/podman-remote /usr/local/bin/podman`) on the dispatcher host.";
            return Task.FromResult(ConfigurationRequirementStatus.Invalid(
                reason,
                suggestion,
                new InvalidOperationException(reason + " " + suggestion)));
        }

        return Task.FromResult(ConfigurationRequirementStatus.Met());
    }
}

/// <summary>
/// Abstracts the "is the configured container runtime binary on PATH?" probe
/// so the requirement can be unit-tested without touching the real filesystem.
/// </summary>
public interface IContainerRuntimeBinaryProbe
{
    /// <summary>
    /// Resolves <paramref name="binaryName"/> against PATH and returns the
    /// first matching absolute path, or <c>null</c> when no directory on
    /// PATH contains an executable file with that name.
    /// </summary>
    /// <param name="binaryName">The bare binary name (e.g. <c>podman</c>).</param>
    /// <param name="cancellationToken">Token observed by long PATH walks.</param>
    string? TryResolveBinary(string binaryName, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IContainerRuntimeBinaryProbe"/> — walks the
/// <c>PATH</c> environment variable in order, returns the first existing
/// file that matches. Platform-aware so Windows test harnesses (and
/// downstream deployments that ship a Windows dispatcher) also work.
/// </summary>
public sealed class ContainerRuntimeBinaryProbe : IContainerRuntimeBinaryProbe
{
    private readonly Func<string?> _pathSource;
    private readonly Func<string, bool> _fileExists;

    /// <summary>
    /// Creates a probe that reads the process-wide <c>PATH</c> and uses the
    /// real filesystem. This is the composition-root overload.
    /// </summary>
    public ContainerRuntimeBinaryProbe()
        : this(() => Environment.GetEnvironmentVariable("PATH"), File.Exists)
    {
    }

    /// <summary>
    /// Creates a probe with injectable <c>PATH</c> reader and file-existence
    /// check. Intended for unit tests that need deterministic behaviour
    /// without mutating the process environment.
    /// </summary>
    public ContainerRuntimeBinaryProbe(Func<string?> pathSource, Func<string, bool> fileExists)
    {
        _pathSource = pathSource ?? throw new ArgumentNullException(nameof(pathSource));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
    }

    /// <inheritdoc />
    public string? TryResolveBinary(string binaryName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(binaryName))
        {
            return null;
        }

        var path = _pathSource();
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var separator = isWindows ? ';' : ':';
        var candidates = isWindows
            ? BuildWindowsCandidates(binaryName)
            : new[] { binaryName };

        foreach (var directory in path.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string trimmed;
            try
            {
                trimmed = directory.Trim();
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                string full;
                try
                {
                    full = Path.Combine(trimmed, candidate);
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry — ignore.
                    continue;
                }

                if (_fileExists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }

    private static string[] BuildWindowsCandidates(string binaryName)
    {
        // On Windows, the literal name may be invoked as `<name>.exe`,
        // `<name>.cmd`, or `<name>.bat`. Honour PATHEXT when set, fall back
        // to the common defaults otherwise.
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExt))
        {
            return new[] { binaryName, binaryName + ".exe", binaryName + ".cmd", binaryName + ".bat" };
        }

        var extensions = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<string>(extensions.Length + 1) { binaryName };
        foreach (var ext in extensions)
        {
            list.Add(binaryName + ext);
        }
        return list.ToArray();
    }
}
