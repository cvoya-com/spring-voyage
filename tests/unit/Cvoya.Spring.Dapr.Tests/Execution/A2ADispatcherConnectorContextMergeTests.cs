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
/// platform-bootstrap names. Under ADR-0055 the connector's per-binding
/// context files ride the agent-bootstrap bundle directly (the bundle
/// provider folds them in), so this merge handles only the env-var portion.
/// </summary>
public class A2ADispatcherConnectorContextMergeTests
{
    private static AgentLaunchSpec MakeSpec(
        Dictionary<string, string>? env = null) =>
        new(EnvironmentVariables: env ?? new Dictionary<string, string>());

    [Fact]
    public void MergeConnectorContext_EmptyContribution_ReturnsSpecUnchanged()
    {
        var spec = MakeSpec(env: new Dictionary<string, string> { ["SPRING_TENANT_ID"] = "x" });
        var result = A2AExecutionDispatcher.MergeConnectorContext(
            spec, ConnectorRuntimeContextContribution.Empty);

        result.ShouldBe(spec);
    }

    [Fact]
    public void MergeConnectorContext_AddsEnvVars()
    {
        var spec = MakeSpec(
            env: new Dictionary<string, string> { ["SPRING_TENANT_ID"] = "x" });
        var contribution = new ConnectorRuntimeContextContribution(
            new Dictionary<string, string> { ["SPRING_CONNECTOR_GITHUB_OWNER"] = "alice" },
            // Files are ignored by MergeConnectorContext under ADR-0055 —
            // they ride the bundle. We supply some here to verify the merge
            // does not accidentally re-introduce a launcher-side context-
            // files surface.
            new Dictionary<string, string> { [".spring/connectors/github/binding.json"] = "{}" });

        var result = A2AExecutionDispatcher.MergeConnectorContext(spec, contribution);

        result.EnvironmentVariables.ShouldContainKeyAndValue("SPRING_TENANT_ID", "x");
        result.EnvironmentVariables.ShouldContainKeyAndValue("SPRING_CONNECTOR_GITHUB_OWNER", "alice");
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
}
