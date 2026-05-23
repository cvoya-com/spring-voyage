// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Mcp;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AgentContextBuilder"/> — D3a / Stage 3 of ADR-0029.
/// Asserts that <see cref="IAgentContextBuilder.BuildAsync"/> emits the full
/// D1-spec env-var set, the ADR-0055 bootstrap env vars, and per-launch
/// credential uniqueness.
/// </summary>
public class AgentContextBuilderTests
{
    private const string AgentId = "test-agent";
    private const string ThreadId = "t-abc";
    private const string McpContainerHost = "host.docker.internal";
    private const int McpPort = 9999;
    private const string BootstrapToken = "bootstrap-token-xyz";

    // ADR-0052 §3: the container-facing MCP endpoint is derived from
    // McpServerOptions (ContainerHost + Port), not the live listener.
    private const string McpEndpoint = "http://host.docker.internal:9999/mcp/";
    private const string McpToken = "mcp-test-token";

    private static IOptions<McpServerOptions> McpOptions() =>
        Options.Create(new McpServerOptions
        {
            ContainerHost = McpContainerHost,
            Port = McpPort,
        });

    private readonly IAgentBootstrapAuthStore _bootstrapAuthStore;
    private readonly AgentContextBuilder _builder;

    public AgentContextBuilderTests()
    {
        var agentContextOptions = Options.Create(new AgentContextOptions
        {
            Bucket2Url = "https://bucket2.example.com",
            LlmProviderUrl = "https://llm.example.com",
            TelemetryUrl = "https://telemetry.example.com",
            TelemetryToken = "telemetry-secret",
        });

        var ollamaOptions = Options.Create(new OllamaOptions
        {
            BaseUrl = "http://spring-ollama:11434",
        });

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _bootstrapAuthStore = Substitute.For<IAgentBootstrapAuthStore>();
        _bootstrapAuthStore.Issue(Arg.Any<string>()).Returns(BootstrapToken);

        _builder = new AgentContextBuilder(
            McpOptions(),
            agentContextOptions,
            ollamaOptions,
            _bootstrapAuthStore,
            loggerFactory);
    }

    private static readonly Guid AcmeTenantId = new("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly string AcmeTenantHex = AcmeTenantId.ToString("N");

    private static AgentLaunchContext MakeLaunchContext(
        Guid? tenantId = null,
        string? unitId = null,
        bool concurrentThreads = true) =>
        new AgentLaunchContext(
            AgentId: AgentId,
            ThreadId: ThreadId,
            Prompt: "do things",
            McpEndpoint: McpEndpoint,
            McpToken: McpToken,
            TenantId: tenantId ?? AcmeTenantId,
            UnitId: unitId,
            ConcurrentThreads: concurrentThreads);

    [Fact]
    public async Task BuildAsync_EmitsRequiredD1SpecEnvVars()
    {
        var ctx = MakeLaunchContext(tenantId: AcmeTenantId, unitId: "u-1");
        var result = await _builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        // Static metadata
        result.EnvironmentVariables.ShouldContainKey("SPRING_TENANT_ID");
        result.EnvironmentVariables["SPRING_TENANT_ID"].ShouldBe(AcmeTenantHex);

        result.EnvironmentVariables.ShouldContainKey("SPRING_AGENT_ID");
        result.EnvironmentVariables["SPRING_AGENT_ID"].ShouldBe(AgentId);

        result.EnvironmentVariables.ShouldContainKey("SPRING_UNIT_ID");
        result.EnvironmentVariables["SPRING_UNIT_ID"].ShouldBe("u-1");

        // Bucket-2
        result.EnvironmentVariables.ShouldContainKey("SPRING_BUCKET2_URL");
        result.EnvironmentVariables["SPRING_BUCKET2_URL"].ShouldBe("https://bucket2.example.com");
        result.EnvironmentVariables.ShouldContainKey("SPRING_BUCKET2_TOKEN");
        result.EnvironmentVariables["SPRING_BUCKET2_TOKEN"].ShouldNotBeNullOrEmpty();

        // LLM provider (operator override takes precedence)
        result.EnvironmentVariables.ShouldContainKey("SPRING_LLM_PROVIDER_URL");
        result.EnvironmentVariables["SPRING_LLM_PROVIDER_URL"].ShouldBe("https://llm.example.com");
        result.EnvironmentVariables.ShouldContainKey("SPRING_LLM_PROVIDER_TOKEN");
        result.EnvironmentVariables["SPRING_LLM_PROVIDER_TOKEN"].ShouldNotBeNullOrEmpty();

        // MCP
        result.EnvironmentVariables.ShouldContainKey("SPRING_MCP_URL");
        result.EnvironmentVariables["SPRING_MCP_URL"].ShouldBe(McpEndpoint);
        result.EnvironmentVariables.ShouldContainKey("SPRING_MCP_TOKEN");
        result.EnvironmentVariables["SPRING_MCP_TOKEN"].ShouldBe(McpToken);

        // Telemetry
        result.EnvironmentVariables.ShouldContainKey("SPRING_TELEMETRY_URL");
        result.EnvironmentVariables["SPRING_TELEMETRY_URL"].ShouldBe("https://telemetry.example.com");
        result.EnvironmentVariables.ShouldContainKey("SPRING_TELEMETRY_TOKEN");
        result.EnvironmentVariables["SPRING_TELEMETRY_TOKEN"].ShouldBe("telemetry-secret");

        // Workspace path
        result.EnvironmentVariables.ShouldContainKey("SPRING_WORKSPACE_PATH");
        result.EnvironmentVariables["SPRING_WORKSPACE_PATH"].ShouldNotBeNullOrEmpty();

        // Concurrent threads
        result.EnvironmentVariables.ShouldContainKey("SPRING_CONCURRENT_THREADS");
        result.EnvironmentVariables["SPRING_CONCURRENT_THREADS"].ShouldBe("true");
    }

    [Fact]
    public async Task BuildAsync_EmitsBootstrapUrlAndToken()
    {
        // ADR-0055 §8 / §9: the agent-sidecar pulls the bundle from a
        // worker bootstrap endpoint authenticated by a per-agent bearer
        // token. The builder stamps both env vars at container launch.
        var ctx = MakeLaunchContext();

        var result = await _builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        result.EnvironmentVariables.ShouldContainKey(AgentWorkspaceContract.BootstrapUrlEnvVar);
        result.EnvironmentVariables[AgentWorkspaceContract.BootstrapUrlEnvVar]
            .ShouldBe($"http://{McpContainerHost}:{McpPort}/v1/bootstrap/agents/{AgentId}");

        result.EnvironmentVariables.ShouldContainKey(AgentWorkspaceContract.BootstrapTokenEnvVar);
        result.EnvironmentVariables[AgentWorkspaceContract.BootstrapTokenEnvVar].ShouldBe(BootstrapToken);
        _bootstrapAuthStore.Received(1).Issue(AgentId);
    }

    [Fact]
    public async Task BuildAsync_OmitsUnitId_WhenNotProvided()
    {
        var ctx = MakeLaunchContext(unitId: null);
        var result = await _builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        result.EnvironmentVariables.ContainsKey("SPRING_UNIT_ID").ShouldBeFalse();
    }

    [Fact]
    public async Task BuildAsync_ConcurrentThreadsFalse_EmitsFalse()
    {
        var ctx = MakeLaunchContext(concurrentThreads: false);
        var result = await _builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        result.EnvironmentVariables["SPRING_CONCURRENT_THREADS"].ShouldBe("false");
    }

    [Fact]
    public async Task BuildAsync_FallsBackToOllamaUrl_WhenLlmProviderUrlNotConfigured()
    {
        var ollamaOptions = Options.Create(new OllamaOptions { BaseUrl = "http://spring-ollama:11434" });
        var agentContextOptions = Options.Create(new AgentContextOptions
        {
            LlmProviderUrl = null, // no override
            Bucket2Url = "https://bucket2.example.com",
        });
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var builder = new AgentContextBuilder(
            McpOptions(),
            agentContextOptions,
            ollamaOptions,
            _bootstrapAuthStore,
            loggerFactory);
        var ctx = MakeLaunchContext();
        var result = await builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        result.EnvironmentVariables["SPRING_LLM_PROVIDER_URL"].ShouldBe("http://spring-ollama:11434");
    }

    [Fact]
    public async Task BuildAsync_MintsFreshTokens_PerLaunch()
    {
        // Two successive calls for the same agent must yield distinct bucket2
        // and LLM-provider tokens — credentials are per-launch, not shared.
        var ctx = MakeLaunchContext();

        var r1 = await _builder.BuildAsync(ctx, TestContext.Current.CancellationToken);
        var r2 = await _builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        r1.EnvironmentVariables["SPRING_BUCKET2_TOKEN"]
            .ShouldNotBe(r2.EnvironmentVariables["SPRING_BUCKET2_TOKEN"]);

        r1.EnvironmentVariables["SPRING_LLM_PROVIDER_TOKEN"]
            .ShouldNotBe(r2.EnvironmentVariables["SPRING_LLM_PROVIDER_TOKEN"]);
    }

    [Fact]
    public async Task BuildAsync_McpToken_IsPassedThrough_NotMinted()
    {
        // The MCP token is supplied via AgentLaunchContext.McpToken and must
        // be forwarded verbatim — the builder must NOT replace it with a
        // freshly minted token. ADR-0052 §3: the deploy path supplies an
        // empty token; dispatch supplies a per-turn session token.
        var ctx = MakeLaunchContext();
        var result = await _builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        result.EnvironmentVariables["SPRING_MCP_TOKEN"].ShouldBe(McpToken);
    }

    [Fact]
    public async Task BuildAsync_McpUrl_ComesFromOptions_NotLaunchContext()
    {
        // ADR-0052 §3: the container-facing MCP endpoint is derived from
        // McpServerOptions (ContainerHost + Port), independent of whatever
        // the launch context carries.
        var ctx = MakeLaunchContext();
        var result = await _builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        result.EnvironmentVariables["SPRING_MCP_URL"].ShouldBe(McpEndpoint);
    }

    [Fact]
    public async Task BuildAsync_McpToken_Empty_WhenLaunchContextHasNoToken()
    {
        // ADR-0052 §3: the persistent-agent deploy path supplies an empty
        // MCP token (no turn context to authorise) — the builder forwards it
        // verbatim rather than minting one.
        var ctx = new AgentLaunchContext(
            AgentId: AgentId,
            ThreadId: ThreadId,
            Prompt: "do things",
            McpEndpoint: McpEndpoint,
            McpToken: string.Empty,
            TenantId: AcmeTenantId);

        var result = await _builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        result.EnvironmentVariables["SPRING_MCP_TOKEN"].ShouldBe(string.Empty);
    }

    [Fact]
    public async Task BuildAsync_OmitsBucket2Url_WhenNotConfigured()
    {
        var ollamaOptions = Options.Create(new OllamaOptions { BaseUrl = "http://spring-ollama:11434" });
        var agentContextOptions = Options.Create(new AgentContextOptions
        {
            Bucket2Url = null, // not configured
        });
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var builder = new AgentContextBuilder(
            McpOptions(),
            agentContextOptions,
            ollamaOptions,
            _bootstrapAuthStore,
            loggerFactory);
        var ctx = MakeLaunchContext();
        var result = await builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        // SPRING_BUCKET2_URL must be absent when not configured — the D1 spec
        // requires it at runtime but the builder should not emit an empty value.
        result.EnvironmentVariables.ContainsKey("SPRING_BUCKET2_URL").ShouldBeFalse();
    }

    [Fact]
    public async Task BuildAsync_EmitsThreadId_WhenProvided()
    {
        // SPRING_THREAD_ID is emitted when the launch carries a thread id
        // from the dispatch context (#1300).
        var ctx = new AgentLaunchContext(
            AgentId: AgentId,
            ThreadId: "thr_abc123",
            Prompt: "do things",
            McpEndpoint: McpEndpoint,
            McpToken: McpToken,
            TenantId: AcmeTenantId);

        var result = await _builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        result.EnvironmentVariables.ShouldContainKey("SPRING_THREAD_ID");
        result.EnvironmentVariables["SPRING_THREAD_ID"].ShouldBe("thr_abc123");
    }

    [Fact]
    public async Task BuildAsync_OmitsThreadId_WhenNotProvided()
    {
        // SPRING_THREAD_ID is absent when the launch context has no thread id
        // (e.g., supervisor-driven restarts are agent-level, not thread-level).
        var ctx = new AgentLaunchContext(
            AgentId: AgentId,
            ThreadId: string.Empty,
            Prompt: "do things",
            McpEndpoint: McpEndpoint,
            McpToken: McpToken,
            TenantId: AcmeTenantId);

        var result = await _builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        result.EnvironmentVariables.ContainsKey("SPRING_THREAD_ID").ShouldBeFalse();
    }
}
