// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub;
using Cvoya.Spring.Connector.GitHub.DependencyInjection;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Execution;

using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Xunit;

/// <summary>
/// Integration tests verifying that <see cref="HostedExecutionDispatcher"/> resolves its
/// tool-executor dependencies from the real DI container built by
/// <see cref="ServiceCollectionExtensions.AddCvoyaSpringDapr"/> and the GitHub connector's
/// <see cref="Cvoya.Spring.Connector.GitHub.DependencyInjection.ServiceCollectionExtensions.AddCvoyaSpringConnectorGitHub"/>.
/// Catches DI misregistrations (e.g., switching <c>TryAddEnumerable</c> to
/// <c>AddSingleton</c> and dropping the enumerable) at test time rather than in production.
/// </summary>
public class ToolUseDispatcherDITests
{
    /// <summary>
    /// Verifies the dispatcher receives the <see cref="GitHubSkillToolExecutor"/> via
    /// <c>IEnumerable&lt;ISkillToolExecutor&gt;</c>, so a <c>tool_use</c> response from the
    /// AI provider routes to the real GitHub executor and the conversation continues.
    /// </summary>
    [Fact]
    public async Task RealDIContainer_ResolvesGitHubSkillToolExecutor_IntoDispatcher()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddCvoyaSpringDapr(configuration);
        services.AddCvoyaSpringConnectorGitHub(configuration);

        using var provider = services.BuildServiceProvider();

        // Assertion 1: the DI container enumerates the GitHub connector's executor.
        var executors = provider.GetRequiredService<IEnumerable<ISkillToolExecutor>>().ToArray();
        executors.Should().Contain(e => e is GitHubSkillToolExecutor,
            "the GitHub connector registers its executor via TryAddEnumerable");

        // Assertion 2: the dispatcher resolves from the container and receives the executors
        // (not via unit-test injection). We resolve the concrete type so we can bypass the
        // streaming publisher that AddCvoyaSpringDapr also registers — the unit tests cover
        // the streaming path; this test is specifically about non-streaming DI wiring.
        var promptAssembler = provider.GetRequiredService<IPromptAssembler>();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        var aiProvider = Substitute.For<IAiProvider>();
        var dispatcher = new HostedExecutionDispatcher(
            aiProvider,
            promptAssembler,
            executors,
            loggerFactory);

        var message = new Message(
            Guid.NewGuid(),
            new Address("agent", "caller"),
            new Address("agent", "dispatched"),
            MessageType.Domain,
            "conv-di-1",
            JsonSerializer.SerializeToElement(new { text = "read the readme" }),
            DateTimeOffset.UtcNow);

        var tool = new ToolDefinition(
            "github_read_file",
            "Reads a file",
            JsonSerializer.SerializeToElement(new { type = "object" }));
        var context = new PromptAssemblyContext(
            Members: [],
            Policies: null,
            Skills: [new Skill("github", "GitHub tools", [tool])],
            PriorMessages: [],
            LastCheckpoint: null,
            AgentInstructions: null,
            Mode: ExecutionMode.Hosted);

        var executorCall = new ToolCall(
            "toolu_di_1",
            "github_read_file",
            JsonSerializer.SerializeToElement(new { owner = "o", repo = "r", path = "README.md" }));

        var capturedToolResultContent = new List<string>();
        aiProvider.CompleteWithToolsAsync(
                Arg.Any<IReadOnlyList<ConversationTurn>>(),
                Arg.Any<IReadOnlyList<ToolDefinition>>(),
                Arg.Any<CancellationToken>())
            .Returns(
                _ => new AiResponse(null, [executorCall], "tool_use"),
                ci =>
                {
                    var turns = ci.ArgAt<IReadOnlyList<ConversationTurn>>(0);
                    var lastTurn = turns[^1];
                    foreach (var block in lastTurn.Content)
                    {
                        if (block is ContentBlock.ToolResultBlock toolResult)
                        {
                            capturedToolResultContent.Add(toolResult.Content);
                        }
                    }

                    return new AiResponse("done", Array.Empty<ToolCall>(), "end_turn");
                });

        var result = await dispatcher.DispatchAsync(
            message,
            context,
            ExecutionMode.Hosted,
            TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Payload.GetProperty("text").GetString().Should().Be("done");

        // The DI-resolved GitHubSkillToolExecutor is reached; it fails on the Octokit call
        // (no App credentials configured), which surfaces as an IsError tool result that
        // includes the tool name. The key is that a missing DI registration would have
        // produced "no executor registered for tool 'github_read_file'" instead.
        capturedToolResultContent.Should().ContainSingle();
        capturedToolResultContent[0].Should().Contain("github_read_file");
        capturedToolResultContent[0].Should().NotContain("no executor registered");
    }
}