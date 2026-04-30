// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AgentContextBuilder"/> — D3a / Stage 3 of ADR-0029.
/// Asserts that <see cref="IAgentContextBuilder.BuildAsync"/> emits the full D1-spec
/// env-var set, the correct context-file names, and per-launch credential uniqueness.
/// </summary>
public class AgentContextBuilderTests
{
    private const string AgentId = "test-agent";
    private const string ThreadId = "t-abc";
    private const string McpEndpoint = "http://host.docker.internal:9999/mcp/";
    private const string McpToken = "mcp-test-token";

    private readonly IMcpServer _mcpServer;
    private readonly AgentContextBuilder _builder;

    public AgentContextBuilderTests()
    {
        _mcpServer = Substitute.For<IMcpServer>();
        _mcpServer.Endpoint.Returns(McpEndpoint);

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

        _builder = new AgentContextBuilder(
            _mcpServer,
            agentContextOptions,
            ollamaOptions,
            loggerFactory);
    }

    private static AgentLaunchContext MakeLaunchContext(
        string? tenantId = "acme",
        string? unitId = null,
        string? agentDefinitionYaml = null,
        string? tenantConfigJson = null,
        bool concurrentThreads = true) =>
        new AgentLaunchContext(
            AgentId: AgentId,
            ThreadId: ThreadId,
            Prompt: "do things",
            McpEndpoint: McpEndpoint,
            McpToken: McpToken,
            TenantId: tenantId ?? "default",
            UnitId: unitId,
            AgentDefinitionYaml: agentDefinitionYaml,
            TenantConfigJson: tenantConfigJson,
            ConcurrentThreads: concurrentThreads);

    [Fact]
    public async Task BuildAsync_EmitsRequiredD1SpecEnvVars()
    {
        var ctx = MakeLaunchContext(tenantId: "acme", unitId: "u-1");
        var result = await _builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        // Static metadata
        result.EnvironmentVariables.ShouldContainKey("SPRING_TENANT_ID");
        result.EnvironmentVariables["SPRING_TENANT_ID"].ShouldBe("acme");

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

        var builder = new AgentContextBuilder(_mcpServer, agentContextOptions, ollamaOptions, loggerFactory);
        var ctx = MakeLaunchContext();
        var result = await builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        result.EnvironmentVariables["SPRING_LLM_PROVIDER_URL"].ShouldBe("http://spring-ollama:11434");
    }

    [Fact]
    public async Task BuildAsync_EmitsAgentDefinitionContextFile_WhenProvided()
    {
        const string yaml = "agent_id: test-agent\nname: Test Agent";
        var ctx = MakeLaunchContext(agentDefinitionYaml: yaml);
        var result = await _builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        result.ContextFiles.ShouldContainKey("agent-definition.yaml");
        result.ContextFiles["agent-definition.yaml"].ShouldBe(yaml);
    }

    [Fact]
    public async Task BuildAsync_EmitsTenantConfigContextFile_WhenProvided()
    {
        const string json = "{\"name\":\"acme\"}";
        var ctx = MakeLaunchContext(tenantConfigJson: json);
        var result = await _builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        result.ContextFiles.ShouldContainKey("tenant-config.json");
        result.ContextFiles["tenant-config.json"].ShouldBe(json);
    }

    [Fact]
    public async Task BuildAsync_EmptyContextFiles_WhenNeitherYamlNorJsonProvided()
    {
        var ctx = MakeLaunchContext(agentDefinitionYaml: null, tenantConfigJson: null);
        var result = await _builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        result.ContextFiles.ShouldBeEmpty();
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
        // The MCP token comes from IMcpServer.IssueSession (pre-minted by
        // the MCP server) and must be forwarded verbatim — the builder must
        // NOT replace it with a freshly minted token.
        var ctx = MakeLaunchContext();
        var result = await _builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        result.EnvironmentVariables["SPRING_MCP_TOKEN"].ShouldBe(McpToken);
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

        var builder = new AgentContextBuilder(_mcpServer, agentContextOptions, ollamaOptions, loggerFactory);
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
            TenantId: "acme");

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
            TenantId: "acme");

        var result = await _builder.BuildAsync(ctx, TestContext.Current.CancellationToken);

        result.EnvironmentVariables.ContainsKey("SPRING_THREAD_ID").ShouldBeFalse();
    }

    // ------------------------------------------------------------------
    // RefreshForRestartAsync — D3d / D1 spec § 2.2.3
    // ------------------------------------------------------------------

    [Fact]
    public async Task RefreshForRestartAsync_EmitsRequiredCredentialEnvVars()
    {
        // The refresh call MUST produce a full credential set (bucket2, llm,
        // mcp) from the agent's persisted identity — no thread id, no prompt.
        _mcpServer.IssueSession(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new McpSession("restart-mcp-token", AgentId, "restart-thread"));

        var restartCtx = new SupervisorRestartContext(
            AgentId: AgentId,
            TenantId: "acme",
            UnitId: "u-eng");

        var result = await _builder.RefreshForRestartAsync(
            restartCtx, TestContext.Current.CancellationToken);

        // Required credential env vars MUST be present.
        result.EnvironmentVariables.ShouldContainKey("SPRING_BUCKET2_TOKEN");
        result.EnvironmentVariables["SPRING_BUCKET2_TOKEN"].ShouldNotBeNullOrEmpty();

        result.EnvironmentVariables.ShouldContainKey("SPRING_LLM_PROVIDER_TOKEN");
        result.EnvironmentVariables["SPRING_LLM_PROVIDER_TOKEN"].ShouldNotBeNullOrEmpty();

        result.EnvironmentVariables.ShouldContainKey("SPRING_MCP_TOKEN");
        result.EnvironmentVariables["SPRING_MCP_TOKEN"].ShouldNotBeNullOrEmpty();

        // Identity env vars MUST be set from the restart context.
        result.EnvironmentVariables["SPRING_TENANT_ID"].ShouldBe("acme");
        result.EnvironmentVariables["SPRING_AGENT_ID"].ShouldBe(AgentId);
        result.EnvironmentVariables["SPRING_UNIT_ID"].ShouldBe("u-eng");

        // SPRING_THREAD_ID MUST NOT be present on a restart (restarts are
        // agent-level, not bound to any user thread).
        result.EnvironmentVariables.ContainsKey("SPRING_THREAD_ID").ShouldBeFalse();
    }

    [Fact]
    public async Task RefreshForRestartAsync_MintsFreshTokens_AcrossSuccessiveCalls()
    {
        // Each RefreshForRestartAsync call MUST produce distinct tokens — the
        // supervisor MUST NOT receive the same token set on two consecutive
        // restarts (D1 spec § 2.2.3 — "no replay of a prior launch's credentials").
        _mcpServer.IssueSession(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => new McpSession($"mcp-{Guid.NewGuid():N}", AgentId, "t"));

        var restartCtx = new SupervisorRestartContext(AgentId: AgentId, TenantId: "acme");

        var r1 = await _builder.RefreshForRestartAsync(
            restartCtx, TestContext.Current.CancellationToken);
        var r2 = await _builder.RefreshForRestartAsync(
            restartCtx, TestContext.Current.CancellationToken);

        r1.EnvironmentVariables["SPRING_BUCKET2_TOKEN"]
            .ShouldNotBe(r2.EnvironmentVariables["SPRING_BUCKET2_TOKEN"]);

        r1.EnvironmentVariables["SPRING_LLM_PROVIDER_TOKEN"]
            .ShouldNotBe(r2.EnvironmentVariables["SPRING_LLM_PROVIDER_TOKEN"]);
    }
}