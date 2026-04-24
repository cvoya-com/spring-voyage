// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ContainerConfigBuilder"/>. The builder is the
/// single seam every dispatch path uses to translate an
/// <see cref="AgentLaunchSpec"/> into a <see cref="ContainerConfig"/>, so
/// these tests pin the field-by-field forwarding contract.
/// </summary>
public class ContainerConfigBuilderTests
{
    private const string Image = "ghcr.io/example/agent:1.2.3";

    private static AgentLaunchSpec MinimalSpec(
        IReadOnlyList<string>? argv = null,
        string? workingDirectory = null,
        IReadOnlyList<string>? extraVolumeMounts = null) =>
        new(
            WorkspaceFiles: new Dictionary<string, string> { ["CLAUDE.md"] = "you are a..." },
            EnvironmentVariables: new Dictionary<string, string> { ["SPRING_SYSTEM_PROMPT"] = "p" },
            WorkspaceMountPath: "/workspace",
            ExtraVolumeMounts: extraVolumeMounts,
            WorkingDirectory: workingDirectory,
            Argv: argv);

    [Fact]
    public void Build_ForwardsImageVerbatim()
    {
        var config = ContainerConfigBuilder.Build(Image, MinimalSpec());

        config.Image.ShouldBe(Image);
    }

    [Fact]
    public void Build_NullArgv_LeavesCommandNull()
    {
        // Today every launcher returns AgentLaunchSpec with Argv == null
        // (the default). The builder must accept that and leave Command
        // null so the container falls back to the image's default
        // ENTRYPOINT/CMD — preserving the no-op semantics until PR 4.
        var config = ContainerConfigBuilder.Build(Image, MinimalSpec(argv: null));

        config.Command.ShouldBeNull();
    }

    [Fact]
    public void Build_EmptyArgv_LeavesCommandNull()
    {
        var config = ContainerConfigBuilder.Build(Image, MinimalSpec(argv: []));

        config.Command.ShouldBeNull();
    }

    [Fact]
    public void Build_NonEmptyArgv_ForwardsToCommand()
    {
        var argv = new[] { "claude", "--mcp-config", "/workspace/.mcp.json" };
        var config = ContainerConfigBuilder.Build(Image, MinimalSpec(argv: argv));

        // Reference equality is acceptable since the spec hands the list
        // straight through; if a future refactor copies it, swap to
        // sequence equality.
        config.Command.ShouldBeSameAs(argv);
    }

    [Fact]
    public void Build_ForwardsEnvironmentVariablesWhenNoExtras()
    {
        var spec = MinimalSpec();

        var config = ContainerConfigBuilder.Build(Image, spec, extraEnv: null);

        // Without extras the builder forwards the spec dictionary as-is.
        config.EnvironmentVariables.ShouldBeSameAs(spec.EnvironmentVariables);
    }

    [Fact]
    public void Build_ForwardsExtraVolumeMounts()
    {
        var mounts = new[] { "/var/run/docker.sock:/var/run/docker.sock" };
        var spec = MinimalSpec(extraVolumeMounts: mounts);

        var config = ContainerConfigBuilder.Build(Image, spec);

        config.VolumeMounts.ShouldBe(mounts);
    }

    [Fact]
    public void Build_AlwaysSetsHostGatewayExtraHost()
    {
        var config = ContainerConfigBuilder.Build(Image, MinimalSpec());

        config.ExtraHosts.ShouldNotBeNull();
        config.ExtraHosts!.ShouldContain("host.docker.internal:host-gateway");
    }

    [Fact]
    public void Build_AppendsExtraHostsAfterBaseline()
    {
        var config = ContainerConfigBuilder.Build(
            Image,
            MinimalSpec(),
            extraHosts: ["custom.example:10.0.0.1"]);

        config.ExtraHosts.ShouldNotBeNull();
        config.ExtraHosts.ShouldBe(
            ["host.docker.internal:host-gateway", "custom.example:10.0.0.1"]);
    }

    [Fact]
    public void Build_NullWorkingDirectory_WithWorkspaceFiles_DefaultsToWorkspaceMountPath()
    {
        // MinimalSpec already sets a non-empty WorkspaceFiles map; the
        // builder treats that as a signal that the launcher (e.g.
        // ClaudeCodeLauncher) wrote files into the workspace and runs its
        // tool from cwd, so the workdir must move to the mount path.
        var config = ContainerConfigBuilder.Build(Image, MinimalSpec(workingDirectory: null));

        config.WorkingDirectory.ShouldBe("/workspace");
    }

    [Fact]
    public void Build_NullWorkingDirectory_WithEmptyWorkspaceFiles_LeavesWorkdirNull()
    {
        // #1159: launchers like DaprAgentLauncher carry an empty workspace
        // (their prompt arrives via env vars, not files) and ship images
        // whose CMD is relative to a fixed image workdir (e.g. /app for
        // `python agent.py`). Overriding workdir to /workspace breaks the
        // relative CMD lookup and the container exits within ~40ms with
        // `python: can't open file '/workspace/agent.py'`.
        var spec = MinimalSpec(workingDirectory: null) with
        {
            WorkspaceFiles = new Dictionary<string, string>(),
        };

        var config = ContainerConfigBuilder.Build(Image, spec);

        config.WorkingDirectory.ShouldBeNull();
    }

    [Fact]
    public void Build_ExplicitWorkingDirectory_AlwaysWins()
    {
        // Whether or not the workspace is materialised, an explicit
        // launcher-supplied WorkingDirectory must be honoured verbatim.
        var withFiles = ContainerConfigBuilder.Build(
            Image,
            MinimalSpec(workingDirectory: "/srv/work"));
        withFiles.WorkingDirectory.ShouldBe("/srv/work");

        var emptySpec = MinimalSpec(workingDirectory: "/srv/work") with
        {
            WorkspaceFiles = new Dictionary<string, string>(),
        };
        var withoutFiles = ContainerConfigBuilder.Build(Image, emptySpec);
        withoutFiles.WorkingDirectory.ShouldBe("/srv/work");
    }

    [Fact]
    public void Build_BuildsWorkspaceFromSpec()
    {
        var spec = MinimalSpec();

        var config = ContainerConfigBuilder.Build(Image, spec);

        config.Workspace.ShouldNotBeNull();
        config.Workspace!.MountPath.ShouldBe(spec.WorkspaceMountPath);
        config.Workspace.Files.ShouldBeSameAs(spec.WorkspaceFiles);
    }

    [Fact]
    public void Build_ExtraEnvIsMergedOnTopOfSpecEnv()
    {
        var spec = MinimalSpec();

        var config = ContainerConfigBuilder.Build(
            Image,
            spec,
            extraEnv: new Dictionary<string, string> { ["EXTRA_KEY"] = "extra-value" });

        config.EnvironmentVariables.ShouldNotBeNull();
        config.EnvironmentVariables!["SPRING_SYSTEM_PROMPT"].ShouldBe("p");
        config.EnvironmentVariables["EXTRA_KEY"].ShouldBe("extra-value");
    }

    [Fact]
    public void Build_ExtraEnvWinsOnKeyCollision()
    {
        // Documented precedence: when extraEnv contains the same key as
        // spec.EnvironmentVariables, the extra wins because callers pass
        // extras to override what the launcher produced.
        var spec = MinimalSpec();

        var config = ContainerConfigBuilder.Build(
            Image,
            spec,
            extraEnv: new Dictionary<string, string> { ["SPRING_SYSTEM_PROMPT"] = "overridden" });

        config.EnvironmentVariables.ShouldNotBeNull();
        config.EnvironmentVariables!["SPRING_SYSTEM_PROMPT"].ShouldBe("overridden");
    }

    [Fact]
    public void Build_NullImage_Throws()
    {
        Should.Throw<ArgumentException>(
            () => ContainerConfigBuilder.Build(image: null!, MinimalSpec()));
    }

    [Fact]
    public void Build_BlankImage_Throws()
    {
        Should.Throw<ArgumentException>(
            () => ContainerConfigBuilder.Build(image: "  ", MinimalSpec()));
    }

    [Fact]
    public void Build_NullSpec_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => ContainerConfigBuilder.Build(Image, spec: null!));
    }
}