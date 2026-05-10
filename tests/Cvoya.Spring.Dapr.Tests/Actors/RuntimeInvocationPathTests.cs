// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="RuntimeInvocationPath"/>. The class encapsulates
/// the runtime-invocation pipeline extracted from <c>AgentActor</c> in
/// ADR-0039 task C1; these tests pin the lean and rich call shapes so the
/// later phases (UnitActor wiring in C2; directory-driven orchestration
/// tools in D2) can extend the seam without regressing the contract.
/// </summary>
public class RuntimeInvocationPathTests
{
    private readonly IAgentDefinitionProvider _definitionProvider = Substitute.For<IAgentDefinitionProvider>();
    private readonly IOrchestrationToolProvider _toolProvider = Substitute.For<IOrchestrationToolProvider>();
    private readonly IAgentDispatchCoordinator _dispatchCoordinator = Substitute.For<IAgentDispatchCoordinator>();
    private readonly ILogger<RuntimeInvocationPath> _logger = Substitute.For<ILogger<RuntimeInvocationPath>>();

    private static Address MakeAgent(string slug) => Address.For(Address.AgentScheme, TestSlugIds.HexFor(slug));

    private static Message MakeMessage(Address from, Address to, string? threadId = null)
        => new(
            Guid.NewGuid(),
            from,
            to,
            MessageType.Domain,
            threadId ?? Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { hello = "world" }),
            DateTimeOffset.UtcNow);

    private RuntimeInvocationPath MakePath(IEnumerable<ISkillRegistry>? skillRegistries = null)
    {
        _toolProvider
            .GetOrchestrationTools(Arg.Any<Address>(), Arg.Any<Guid>())
            .Returns(Array.Empty<OrchestrationToolDescriptor>());

        return new RuntimeInvocationPath(
            _definitionProvider,
            skillRegistries ?? Array.Empty<ISkillRegistry>(),
            _toolProvider,
            _dispatchCoordinator,
            _logger);
    }

    [Fact]
    public async Task InvokeAsync_LeanOverload_DispatchesViaCoordinator()
    {
        var subject = MakeAgent("test-agent");
        var sender = MakeAgent("test-sender");
        var inbound = MakeMessage(sender, subject);
        var path = MakePath();

        await path.InvokeAsync(subject, inbound, TestContext.Current.CancellationToken);

        await _dispatchCoordinator.Received(1).RunDispatchAsync(
            agentId: subject.Path,
            message: inbound,
            context: Arg.Any<PromptAssemblyContext>(),
            emitActivity: Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
            onDispatchExit: Arg.Any<Func<string, Task>>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_LeanOverload_PullsAgentInstructionsFromDefinition()
    {
        var subject = MakeAgent("test-agent");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject);
        _definitionProvider.GetByIdAsync(subject.Path, Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(subject.Path, "Test", "Be helpful.", null));
        var path = MakePath();

        PromptAssemblyContext? captured = null;
        await _dispatchCoordinator.RunDispatchAsync(
            Arg.Any<string>(),
            Arg.Any<Message>(),
            Arg.Do<PromptAssemblyContext>(ctx => captured = ctx),
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken>());

        await path.InvokeAsync(subject, inbound, TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.AgentInstructions.ShouldBe("Be helpful.");
    }

    [Fact]
    public async Task InvokeAsync_LeanOverload_AssemblesSkillsFromRegistries()
    {
        var subject = MakeAgent("test-agent");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject);
        var registry = Substitute.For<ISkillRegistry>();
        registry.Name.Returns("github");
        registry.GetToolDefinitions().Returns([
            new ToolDefinition("github_comment", "Comment", JsonSerializer.SerializeToElement(new { }))
        ]);
        var path = MakePath([registry]);

        PromptAssemblyContext? captured = null;
        await _dispatchCoordinator.RunDispatchAsync(
            Arg.Any<string>(),
            Arg.Any<Message>(),
            Arg.Do<PromptAssemblyContext>(ctx => captured = ctx),
            Arg.Any<Func<ActivityEvent, CancellationToken, Task>>(),
            Arg.Any<Func<string, Task>>(),
            Arg.Any<CancellationToken>());

        await path.InvokeAsync(subject, inbound, TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.Skills.ShouldNotBeNull();
        captured.Skills!.Count.ShouldBe(1);
        captured.Skills[0].Name.ShouldBe("github");
        captured.Skills[0].Tools.Count.ShouldBe(1);
    }

    [Fact]
    public async Task InvokeAsync_LeanOverload_ResolvesOrchestrationToolsForThread()
    {
        var subject = MakeAgent("test-agent");
        var threadId = Guid.NewGuid();
        var inbound = MakeMessage(MakeAgent("test-sender"), subject, threadId.ToString());
        var path = MakePath();

        await path.InvokeAsync(subject, inbound, TestContext.Current.CancellationToken);

        _toolProvider.Received(1).GetOrchestrationTools(subject, threadId);
    }

    [Fact]
    public async Task InvokeAsync_LeanOverload_NonGuidThreadIdFallsBackToEmpty()
    {
        var subject = MakeAgent("test-agent");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject, "not-a-guid");
        var path = MakePath();

        await path.InvokeAsync(subject, inbound, TestContext.Current.CancellationToken);

        _toolProvider.Received(1).GetOrchestrationTools(subject, Guid.Empty);
    }

    [Fact]
    public async Task InvokeAsync_RichOverload_ForwardsContextAndDelegatesUnchanged()
    {
        var subject = MakeAgent("test-agent");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject);
        var path = MakePath();

        var context = new PromptAssemblyContext(
            Members: Array.Empty<Address>(),
            Policies: null,
            Skills: null,
            PriorMessages: [inbound],
            LastCheckpoint: null,
            AgentInstructions: "instructions",
            EffectiveMetadata: null,
            PendingAmendments: null);

        Func<ActivityEvent, CancellationToken, Task> emit = (_, _) => Task.CompletedTask;
        Func<string, Task> clear = _ => Task.CompletedTask;

        await path.InvokeAsync(subject, inbound, context, emit, clear, TestContext.Current.CancellationToken);

        await _dispatchCoordinator.Received(1).RunDispatchAsync(
            agentId: subject.Path,
            message: inbound,
            context: context,
            emitActivity: emit,
            onDispatchExit: clear,
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_RichOverload_DoesNotConsultDefinitionOrToolProvider()
    {
        var subject = MakeAgent("test-agent");
        var inbound = MakeMessage(MakeAgent("test-sender"), subject);
        var path = MakePath();

        var context = new PromptAssemblyContext(
            Members: Array.Empty<Address>(),
            Policies: null,
            Skills: null,
            PriorMessages: Array.Empty<Message>(),
            LastCheckpoint: null,
            AgentInstructions: null);

        await path.InvokeAsync(
            subject, inbound, context,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        await _definitionProvider.DidNotReceive().GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _toolProvider.DidNotReceive().GetOrchestrationTools(Arg.Any<Address>(), Arg.Any<Guid>());
    }
}
