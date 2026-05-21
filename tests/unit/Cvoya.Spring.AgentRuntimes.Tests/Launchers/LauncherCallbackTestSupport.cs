// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Tests.Launchers;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Runtime;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

internal sealed class LauncherCallbackTestSupport
{
    private const string CallbackUrl = "http://dispatcher.example/root/";

    public const string OrchestrationMcpUrl = "http://dispatcher.example/root/v1/runtime/orchestration";

    private static readonly byte[] SigningKey =
    [
        0x30, 0x9d, 0xe3, 0xf8, 0x4d, 0x02, 0x5d, 0xaf,
        0x76, 0x11, 0xc8, 0x96, 0x4e, 0x61, 0x73, 0x0b,
        0x44, 0x8e, 0x26, 0x74, 0x95, 0xe2, 0xab, 0x19,
        0xda, 0xc4, 0x31, 0x82, 0x07, 0xbd, 0x58, 0x6f,
    ];

    private readonly CallbackTokenValidator _validator;

    public LauncherCallbackTestSupport()
    {
        var keyProvider = Substitute.For<ITenantSigningKeyProvider>();
        keyProvider.GetSigningKey(Arg.Any<Guid>()).Returns(SigningKey);

        var options = Options.Create(new CallbackTokenOptions());
        var issuer = new CallbackTokenIssuer(keyProvider, options);
        Builder = new DispatcherCallbackEnvironmentBuilder(
            Options.Create(new OrchestrationCallbackOptions
            {
                BaseUrl = CallbackUrl,
            }),
            issuer);
        _validator = new CallbackTokenValidator(keyProvider, options);
    }

    public IAgentCallbackEnvironmentBuilder Builder { get; }

    public static Address DefaultAgentAddress { get; } =
        new(Address.AgentScheme, Guid.Parse("aaaaaaaa-aaaa-4aaa-aaaa-aaaaaaaaaaaa"));

    public static Guid DefaultThreadId { get; } =
        Guid.Parse("bbbbbbbb-bbbb-4bbb-bbbb-bbbbbbbbbbbb");

    public static Guid DefaultMessageId { get; } =
        Guid.Parse("cccccccc-cccc-4ccc-cccc-cccccccccccc");

    public static AgentLaunchContext CreateContext(
        string? agentId = null,
        Guid? threadId = null,
        Guid? messageId = null,
        Address? agentAddress = null,
        string prompt = "## Platform Instructions\nBe helpful.",
        string mcpEndpoint = "http://host.docker.internal:9999/mcp/",
        string mcpToken = "top-secret-token",
        Guid? tenantId = null,
        string? unitId = null,
        string? provider = null,
        string? model = null)
    {
        var resolvedAddress = agentAddress ?? DefaultAgentAddress;
        var resolvedThreadId = threadId ?? DefaultThreadId;

        return new AgentLaunchContext(
            AgentId: agentId ?? resolvedAddress.Path,
            ThreadId: resolvedThreadId.ToString("N"),
            Prompt: prompt,
            McpEndpoint: mcpEndpoint,
            McpToken: mcpToken,
            TenantId: tenantId ?? Cvoya.Spring.Core.Tenancy.OssTenantIds.Default,
            UnitId: unitId,
            // #2096 / ADR-0041: the safe-default mode is
            // `concurrent_threads: false` — tests that need to assert the
            // CLI launcher's prompt-guard composition must opt in
            // explicitly via `with { ConcurrentThreads = true }`. Keeps
            // existing prompt-equality assertions valid.
            ConcurrentThreads: false,
            Provider: provider,
            Model: model,
            AgentAddress: resolvedAddress,
            CallbackThreadId: resolvedThreadId,
            MessageId: messageId ?? DefaultMessageId);
    }

    public void AssertCallbackEnvironment(AgentLaunchSpec spec, AgentLaunchContext context)
    {
        spec.EnvironmentVariables[AgentCallbackEnvironmentContract.CallbackUrlEnvVar]
            .ShouldBe(CallbackUrl);
        spec.EnvironmentVariables.ShouldContainKey(AgentCallbackEnvironmentContract.CallbackTokenEnvVar);

        var token = _validator.Validate(
            spec.EnvironmentVariables[AgentCallbackEnvironmentContract.CallbackTokenEnvVar]);

        token.TenantId.ShouldBe(context.TenantId);
        token.AgentAddress.ShouldBe(context.AgentAddress);
        token.ThreadId.ShouldBe(context.CallbackThreadId!.Value);
        token.MessageId.ShouldBe(context.MessageId!.Value);
        token.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }
}
