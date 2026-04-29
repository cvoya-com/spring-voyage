// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Orchestration;

using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AiOrchestrationStrategy"/>.
/// </summary>
public class AiOrchestrationStrategyTests
{
    private readonly IAiProvider _aiProvider = Substitute.For<IAiProvider>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IUnitContext _context = Substitute.For<IUnitContext>();
    private readonly AiOrchestrationStrategy _strategy;

    public AiOrchestrationStrategyTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _strategy = new AiOrchestrationStrategy(_aiProvider, _loggerFactory);

        _context.UnitAddress.Returns(new Address("unit", "test-unit"));
    }

    private static Message CreateMessage(
        MessageType type = MessageType.Domain,
        string? threadId = null)
    {
        return new Message(
            Guid.NewGuid(),
            new Address("agent", "test-sender"),
            new Address("unit", "test-unit"),
            type,
            threadId ?? Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Task = "process data" }),
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task OrchestrateAsync_CallsAiProviderWithMemberContext()
    {
        var member1 = new Address("agent", "agent-1");
        var member2 = new Address("agent", "agent-2");
        _context.Members.Returns([member1, member2]);
        _aiProvider.CompleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("agent://agent-1");

        var message = CreateMessage();
        await _strategy.OrchestrateAsync(message, _context, TestContext.Current.CancellationToken);

        await _aiProvider.Received(1).CompleteAsync(
            Arg.Is<string>(p => p.Contains("agent://agent-1") && p.Contains("agent://agent-2")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrchestrateAsync_ForwardsToMemberReturnedByAi()
    {
        var member1 = new Address("agent", "agent-1");
        var member2 = new Address("agent", "agent-2");
        _context.Members.Returns([member1, member2]);
        _aiProvider.CompleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("agent://agent-2");

        var expectedResponse = CreateMessage(threadId: "response");
        _context.SendAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(expectedResponse);

        var message = CreateMessage();
        var result = await _strategy.OrchestrateAsync(message, _context, TestContext.Current.CancellationToken);

        result.ShouldBe(expectedResponse);
        await _context.Received(1).SendAsync(
            Arg.Is<Message>(m => m.To == member2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrchestrateAsync_ReturnsNull_WhenAiResponseDoesNotMatchAnyMember()
    {
        var member1 = new Address("agent", "agent-1");
        _context.Members.Returns([member1]);
        _aiProvider.CompleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("unknown://nonexistent");

        var message = CreateMessage();
        var result = await _strategy.OrchestrateAsync(message, _context, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        await _context.DidNotReceive().SendAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrchestrateAsync_ReturnsNull_WhenNoMembers()
    {
        _context.Members.Returns([]);

        var message = CreateMessage();
        var result = await _strategy.OrchestrateAsync(message, _context, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        await _aiProvider.DidNotReceive().CompleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void BuildRoutingPrompt_IncludesAllMembers()
    {
        var member1 = new Address("agent", "agent-1");
        var member2 = new Address("unit", "sub-unit");
        _context.Members.Returns([member1, member2]);

        var message = CreateMessage();
        var prompt = AiOrchestrationStrategy.BuildRoutingPrompt(message, _context);

        prompt.ShouldContain("agent://agent-1");
        prompt.ShouldContain("unit://sub-unit");
        prompt.ShouldContain("process data");
    }

    [Fact]
    public void ParseRoutingDecision_FindsMatchingMember()
    {
        var member1 = new Address("agent", "agent-1");
        var member2 = new Address("agent", "agent-2");
        var members = new List<Address> { member1, member2 };

        var result = AiOrchestrationStrategy.ParseRoutingDecision("agent://agent-2", members);

        result.ShouldBe(member2);
    }

    [Fact]
    public void ParseRoutingDecision_ReturnsNull_WhenNoMatch()
    {
        var member1 = new Address("agent", "agent-1");
        var members = new List<Address> { member1 };

        var result = AiOrchestrationStrategy.ParseRoutingDecision("unknown://foo", members);

        result.ShouldBeNull();
    }
}