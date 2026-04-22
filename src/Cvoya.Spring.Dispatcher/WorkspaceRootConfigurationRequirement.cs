// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher;

using Cvoya.Spring.Core.Configuration;

using Microsoft.Extensions.Options;

/// <summary>
/// Tier-1 requirement: the directory named by <see cref="DispatcherOptions.WorkspaceRoot"/>
/// must exist on the dispatcher host filesystem and be writable. The
/// dispatcher materialises per-invocation agent workspaces here and
/// bind-mounts them into agent containers — so a missing or read-only root
/// would make every <c>POST /v1/containers</c> with a workspace fail. Surfacing
/// the misconfiguration at boot beats a runtime 500 storm. See issue #1042.
/// </summary>
public sealed class WorkspaceRootConfigurationRequirement(
    IOptions<DispatcherOptions> optionsAccessor,
    IWorkspaceRootProbe probe) : IConfigurationRequirement
{
    private readonly IOptions<DispatcherOptions> _options =
        optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
    private readonly IWorkspaceRootProbe _probe =
        probe ?? throw new ArgumentNullException(nameof(probe));

    /// <inheritdoc />
    public string RequirementId => "dispatcher-workspace-root";

    /// <inheritdoc />
    public string DisplayName => "Dispatcher workspace root is writable";

    /// <inheritdoc />
    public string SubsystemName => "Dispatcher";

    /// <inheritdoc />
    public bool IsMandatory => true;

    /// <inheritdoc />
    public IReadOnlyList<string> EnvironmentVariableNames { get; } =
        new[] { "Dispatcher__WorkspaceRoot" };

    /// <inheritdoc />
    public string? ConfigurationSectionPath => DispatcherOptions.SectionName + ":WorkspaceRoot";

    /// <inheritdoc />
    public string Description =>
        "The directory named by Dispatcher:WorkspaceRoot must exist on the dispatcher host and be writable. The dispatcher materialises per-invocation agent workspaces here and bind-mounts them into agent containers (issue #1042).";

    /// <inheritdoc />
    public Uri? DocumentationUrl { get; } =
        new Uri("https://github.com/cvoya-com/spring-voyage/blob/main/docs/architecture/deployment.md", UriKind.Absolute);

    /// <inheritdoc />
    public Task<ConfigurationRequirementStatus> ValidateAsync(CancellationToken cancellationToken)
    {
        var root = _options.Value.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            var reason = "Dispatcher:WorkspaceRoot is not configured.";
            var suggestion = "Set Dispatcher:WorkspaceRoot to a writable host path (default: "
                + DispatcherOptions.DefaultWorkspaceRoot + ").";
            return Task.FromResult(ConfigurationRequirementStatus.Invalid(
                reason, suggestion, new InvalidOperationException(reason + " " + suggestion)));
        }

        var (ok, error) = _probe.Probe(root, cancellationToken);
        if (ok)
        {
            return Task.FromResult(ConfigurationRequirementStatus.Met());
        }

        var failureReason =
            $"Dispatcher:WorkspaceRoot '{root}' is not usable: {error}. " +
            "Workspace bind-mounts for agent containers will fail.";
        var failureSuggestion =
            $"Create the directory and grant the dispatcher user write access (e.g. `mkdir -p {root} && chown spring:spring {root}`), " +
            "or bind-mount a host volume into the dispatcher container at this path.";
        return Task.FromResult(ConfigurationRequirementStatus.Invalid(
            failureReason,
            failureSuggestion,
            new InvalidOperationException(failureReason + " " + failureSuggestion)));
    }
}

/// <summary>
/// Abstracts "is this directory present and writable?" so the requirement is
/// unit-testable without touching the real filesystem.
/// </summary>
public interface IWorkspaceRootProbe
{
    /// <summary>
    /// Returns <c>(true, null)</c> when <paramref name="path"/> exists (or
    /// can be created) and a temp file write succeeded; otherwise
    /// <c>(false, errorMessage)</c>.
    /// </summary>
    (bool Ok, string? Error) Probe(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IWorkspaceRootProbe"/>: ensures the directory exists
/// (creating it when missing) and verifies the dispatcher process can write a
/// throwaway probe file into it.
/// </summary>
public sealed class WorkspaceRootProbe : IWorkspaceRootProbe
{
    /// <inheritdoc />
    public (bool Ok, string? Error) Probe(string path, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            return (false, $"could not create directory: {ex.Message}");
        }

        var probeFile = Path.Combine(path, ".spring-dispatcher-write-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probeFile, "ok");
            File.Delete(probeFile);
        }
        catch (Exception ex)
        {
            return (false, $"write probe failed: {ex.Message}");
        }

        return (true, null);
    }
}