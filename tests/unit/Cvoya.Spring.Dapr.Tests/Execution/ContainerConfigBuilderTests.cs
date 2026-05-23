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
            EnvironmentVariables: new Dictionary<string, string> { ["SPRING_SYSTEM_PROMPT"] = "p" },
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
    public void Build_NullWorkingDirectory_LeavesWorkdirNull()
    {
        // ADR-0055: the launcher no longer carries WorkspaceFiles, so the
        // builder never auto-overrides the workdir. The dispatcher
        // supplies the per-member volume mount; the image's WORKDIR (or
        // an explicit spec.WorkingDirectory) decides the cwd.
        var config = ContainerConfigBuilder.Build(Image, MinimalSpec(workingDirectory: null));

        config.WorkingDirectory.ShouldBeNull();
    }

    [Fact]
    public void Build_ExplicitWorkingDirectory_IsHonouredVerbatim()
    {
        var config = ContainerConfigBuilder.Build(
            Image,
            MinimalSpec(workingDirectory: "/srv/work"));

        config.WorkingDirectory.ShouldBe("/srv/work");
    }

    [Fact]
    public void Build_DoesNotEmitWorkspaceField_AfterAdr0055()
    {
        // ADR-0055: the dispatcher does not materialise workspace files;
        // the sidecar pulls the bundle and writes files under the
        // per-member workspace volume. ContainerConfig no longer carries a
        // Workspace field at all — the contract is enforced at compile
        // time, this test pins the absence at runtime for the reviewer.
        var config = ContainerConfigBuilder.Build(Image, MinimalSpec());

        typeof(ContainerConfig).GetProperty("Workspace").ShouldBeNull(
            "ContainerConfig.Workspace is gone post-ADR-0055");
        config.ShouldNotBeNull();
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

    [Fact]
    public void Build_AttachesContainerToTenantBridge()
    {
        // ADR 0028 — Decision A / issue #1160: agent containers must
        // attach to the per-tenant bridge instead of podman's default
        // network so tenant traffic cannot reach platform-only services
        // (postgres / redis / API / web). OSS resolves every agent to
        // a single shared spring-tenant-default network.
        var config = ContainerConfigBuilder.Build(Image, MinimalSpec());

        config.NetworkName.ShouldBe(ContainerConfigBuilder.TenantNetworkName);
        config.NetworkName.ShouldBe("spring-tenant-default");
    }
}
