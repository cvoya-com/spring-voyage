// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for the static <see cref="A2AExecutionDispatcher.MergeConnectorContext"/>
/// helper (#2380) — the seam that merges the connector-context contribution
/// onto a bootstrap-merged launch spec and fails fast on any collision with
/// platform-bootstrap names.
/// </summary>
public class A2ADispatcherConnectorContextMergeTests
{
    private static AgentLaunchSpec MakeSpec(
        Dictionary<string, string>? env = null,
        Dictionary<string, string>? contextFiles = null) =>
        new(
            WorkspaceFiles: new Dictionary<string, string>(),
            EnvironmentVariables: env ?? new Dictionary<string, string>(),
            WorkspaceMountPath: "/workspace",
            ContextFiles: contextFiles);

    [Fact]
    public void MergeConnectorContext_EmptyContribution_ReturnsSpecUnchanged()
    {
        var spec = MakeSpec(env: new Dictionary<string, string> { ["SPRING_TENANT_ID"] = "x" });
        var result = A2AExecutionDispatcher.MergeConnectorContext(
            spec, ConnectorRuntimeContextContribution.Empty);

        result.ShouldBe(spec);
    }

    [Fact]
    public void MergeConnectorContext_AddsEnvVarsAndFiles()
    {
        var spec = MakeSpec(
            env: new Dictionary<string, string> { ["SPRING_TENANT_ID"] = "x" },
            contextFiles: new Dictionary<string, string> { ["agent-definition.yaml"] = "agent:" });
        var contribution = new ConnectorRuntimeContextContribution(
            new Dictionary<string, string> { ["SPRING_CONNECTOR_GITHUB_OWNER"] = "alice" },
            new Dictionary<string, string> { ["connectors/github/binding.json"] = "{}" });

        var result = A2AExecutionDispatcher.MergeConnectorContext(spec, contribution);

        result.EnvironmentVariables.ShouldContainKeyAndValue("SPRING_TENANT_ID", "x");
        result.EnvironmentVariables.ShouldContainKeyAndValue("SPRING_CONNECTOR_GITHUB_OWNER", "alice");
        result.ContextFiles.ShouldNotBeNull();
        result.ContextFiles!.ShouldContainKey("agent-definition.yaml");
        result.ContextFiles.ShouldContainKey("connectors/github/binding.json");
    }

    [Fact]
    public void MergeConnectorContext_EnvCollidesWithBootstrap_Throws()
    {
        var spec = MakeSpec(
            env: new Dictionary<string, string> { ["SPRING_TENANT_ID"] = "platform" });
        // A pathological contributor that tries to shadow a platform name.
        var contribution = new ConnectorRuntimeContextContribution(
            new Dictionary<string, string> { ["SPRING_TENANT_ID"] = "evil" },
            new Dictionary<string, string>());

        var ex = Should.Throw<SpringException>(() =>
            A2AExecutionDispatcher.MergeConnectorContext(spec, contribution));

        ex.Message.ShouldContain("SPRING_TENANT_ID");
    }

    [Fact]
    public void MergeConnectorContext_FileCollidesWithBootstrap_Throws()
    {
        var spec = MakeSpec(
            contextFiles: new Dictionary<string, string> { ["tenant-config.json"] = "{}" });
        var contribution = new ConnectorRuntimeContextContribution(
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["tenant-config.json"] = "evil" });

        var ex = Should.Throw<SpringException>(() =>
            A2AExecutionDispatcher.MergeConnectorContext(spec, contribution));

        ex.Message.ShouldContain("tenant-config.json");
    }

    [Fact]
    public void MergeConnectorContext_LauncherSpecWithoutContextFiles_PopulatesFromContribution()
    {
        var spec = MakeSpec(); // no context files
        var contribution = new ConnectorRuntimeContextContribution(
            new Dictionary<string, string> { ["SPRING_CONNECTOR_GITHUB_OWNER"] = "alice" },
            new Dictionary<string, string> { ["connectors/github/binding.json"] = "{}" });

        var result = A2AExecutionDispatcher.MergeConnectorContext(spec, contribution);

        result.ContextFiles.ShouldNotBeNull();
        result.ContextFiles!["connectors/github/binding.json"].ShouldBe("{}");
    }
}
