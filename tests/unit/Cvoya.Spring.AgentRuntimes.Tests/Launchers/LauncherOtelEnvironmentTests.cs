// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Tests.Launchers;

using Cvoya.Spring.AgentRuntimes.Launchers;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;

using Shouldly;

using Xunit;

/// <summary>
/// Issue #2492 — verifies the launcher injects the OTLP env vars the
/// runtime container reads to ship spans / logs to <c>/otlp/v1/</c>.
/// </summary>
public class LauncherOtelEnvironmentTests
{
    private static readonly Guid TenantA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid AgentA = new("11111111-0000-0000-0000-000000000001");

    [Fact]
    public void Add_InjectsEndpointProtocolAndResourceAttributes()
    {
        var envVars = new Dictionary<string, string>
        {
            [AgentCallbackEnvironmentContract.CallbackUrlEnvVar] = "https://platform.example.com/v1/runtime/orchestration",
            [AgentCallbackEnvironmentContract.CallbackTokenEnvVar] = "the-jwt-token",
        };
        var context = NewContext();

        LauncherOtelEnvironment.Add(context, envVars);

        envVars[LauncherOtelEnvironment.OtlpEndpointEnvVar].ShouldBe("https://platform.example.com/otlp");
        envVars[LauncherOtelEnvironment.OtlpProtocolEnvVar].ShouldBe("http/json");
        envVars[LauncherOtelEnvironment.OtlpHeadersEnvVar].ShouldBe("Authorization=Bearer the-jwt-token");
        envVars[LauncherOtelEnvironment.OtlpResourceAttributesEnvVar].ShouldContain("sv.tenant.id=");
        envVars[LauncherOtelEnvironment.OtlpResourceAttributesEnvVar].ShouldContain("sv.subject.uuid=");
        envVars[LauncherOtelEnvironment.OtlpResourceAttributesEnvVar].ShouldContain("sv.subject.kind=agent");
        envVars[LauncherOtelEnvironment.OtlpServiceNameEnvVar].ShouldBe("spring-voyage/agent");
    }

    [Fact]
    public void Add_HumanSubject_StampsSubjectKindHuman()
    {
        var envVars = new Dictionary<string, string>
        {
            [AgentCallbackEnvironmentContract.CallbackUrlEnvVar] = "https://platform.example.com/v1/runtime/orchestration",
            [AgentCallbackEnvironmentContract.CallbackTokenEnvVar] = "tok",
        };
        var humanId = new Guid("22222222-0000-0000-0000-000000000001");
        var context = NewContext() with
        {
            AgentAddress = new Address(Address.HumanScheme, humanId),
        };

        LauncherOtelEnvironment.Add(context, envVars);

        envVars[LauncherOtelEnvironment.OtlpResourceAttributesEnvVar].ShouldContain("sv.subject.kind=human");
        envVars[LauncherOtelEnvironment.OtlpServiceNameEnvVar].ShouldBe("spring-voyage/human");
    }

    [Fact]
    public void Add_MissingCallbackUrl_NoOps()
    {
        var envVars = new Dictionary<string, string>();
        var context = NewContext();

        LauncherOtelEnvironment.Add(context, envVars);

        envVars.ShouldNotContainKey(LauncherOtelEnvironment.OtlpEndpointEnvVar);
        envVars.ShouldNotContainKey(LauncherOtelEnvironment.OtlpProtocolEnvVar);
    }

    private static AgentLaunchContext NewContext()
        => new(
            AgentId: AgentA.ToString("N"),
            ThreadId: Guid.NewGuid().ToString("N"),
            Prompt: "system prompt",
            McpEndpoint: "http://mcp",
            McpToken: "mcp-token",
            TenantId: TenantA,
            UnitId: null,
            AgentAddress: new Address(Address.AgentScheme, AgentA),
            CallbackThreadId: Guid.NewGuid(),
            MessageId: Guid.NewGuid());
}
