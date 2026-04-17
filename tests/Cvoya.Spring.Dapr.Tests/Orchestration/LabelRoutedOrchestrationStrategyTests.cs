// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using System.Collections.Generic;
using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Dapr.Orchestration;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="LabelRoutedOrchestrationStrategy"/> (#389).
/// Covers the three acceptance paths: no-label drop, match-and-forward,
/// and misconfigured-path drop; plus label-extraction helpers for both
/// payload shapes the strategy supports (bare string labels and GitHub
/// webhook objects).
/// </summary>
public class LabelRoutedOrchestrationStrategyTests
{
    private readonly IUnitPolicyRepository _policyRepository = Substitute.For<IUnitPolicyRepository>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IUnitContext _context = Substitute.For<IUnitContext>();
    private readonly LabelRoutedOrchestrationStrategy _strategy;

    public LabelRoutedOrchestrationStrategyTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _strategy = new LabelRoutedOrchestrationStrategy(_policyRepository, _loggerFactory);
        _context.UnitAddress.Returns(new Address("unit", "engineering-team"));
    }

    private static Message CreateMessage(object payload) =>
        new(
            Guid.NewGuid(),
            new Address("connector", "github"),
            new Address("unit", "engineering-team"),
            MessageType.Domain,
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(payload),
            DateTimeOffset.UtcNow);

    [Fact]
    public async Task OrchestrateAsync_NoMembers_ReturnsNullAndDoesNotReadPolicy()
    {
        _context.Members.Returns([]);

        var result = await _strategy.OrchestrateAsync(
            CreateMessage(new { labels = new[] { "agent:backend" } }),
            _context,
            TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        await _policyRepository.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrchestrateAsync_NoLabelRoutingPolicy_DropsMessage()
    {
        _context.Members.Returns([new Address("agent", "backend-engineer")]);
        _policyRepository
            .GetAsync("engineering-team", Arg.Any<CancellationToken>())
            .Returns(UnitPolicy.Empty);

        var result = await _strategy.OrchestrateAsync(
            CreateMessage(new { labels = new[] { "agent:backend" } }),
            _context,
            TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        await _context.DidNotReceive().SendAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrchestrateAsync_UnlabeledMessage_DropsWithoutDispatch()
    {
        _context.Members.Returns([new Address("agent", "backend-engineer")]);
        _policyRepository
            .GetAsync("engineering-team", Arg.Any<CancellationToken>())
            .Returns(new UnitPolicy(LabelRouting: new LabelRoutingPolicy(
                TriggerLabels: new Dictionary<string, string>
                {
                    ["agent:backend"] = "backend-engineer",
                })));

        var result = await _strategy.OrchestrateAsync(
            CreateMessage(new { title = "Issue without labels" }),
            _context,
            TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        await _context.DidNotReceive().SendAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrchestrateAsync_MatchingLabel_ForwardsToMappedMember()
    {
        var target = new Address("agent", "backend-engineer");
        _context.Members.Returns([
            new Address("agent", "qa-engineer"),
            target,
        ]);
        _policyRepository
            .GetAsync("engineering-team", Arg.Any<CancellationToken>())
            .Returns(new UnitPolicy(LabelRouting: new LabelRoutingPolicy(
                TriggerLabels: new Dictionary<string, string>
                {
                    ["agent:qa"] = "qa-engineer",
                    ["agent:backend"] = "backend-engineer",
                })));

        var sent = CreateMessage(new { acknowledged = true });
        _context.SendAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns(sent);

        var result = await _strategy.OrchestrateAsync(
            CreateMessage(new { labels = new[] { "agent:backend" } }),
            _context,
            TestContext.Current.CancellationToken);

        result.ShouldBe(sent);
        await _context.Received(1).SendAsync(
            Arg.Is<Message>(m => m.To == target),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrchestrateAsync_GitHubWebhookPayloadShape_ExtractsLabelName()
    {
        var target = new Address("agent", "backend-engineer");
        _context.Members.Returns([target]);
        _policyRepository
            .GetAsync("engineering-team", Arg.Any<CancellationToken>())
            .Returns(new UnitPolicy(LabelRouting: new LabelRoutingPolicy(
                TriggerLabels: new Dictionary<string, string>
                {
                    ["agent:backend"] = "backend-engineer",
                })));

        // GitHub-shape payload: labels is an array of objects with a name field.
        var result = await _strategy.OrchestrateAsync(
            CreateMessage(new
            {
                action = "opened",
                labels = new[]
                {
                    new { name = "bug" },
                    new { name = "agent:backend" },
                },
            }),
            _context,
            TestContext.Current.CancellationToken);

        await _context.Received(1).SendAsync(
            Arg.Is<Message>(m => m.To == target),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrchestrateAsync_LabelMatchesCaseInsensitively()
    {
        var target = new Address("agent", "backend-engineer");
        _context.Members.Returns([target]);
        _policyRepository
            .GetAsync("engineering-team", Arg.Any<CancellationToken>())
            .Returns(new UnitPolicy(LabelRouting: new LabelRoutingPolicy(
                TriggerLabels: new Dictionary<string, string>
                {
                    ["Agent:Backend"] = "Backend-Engineer",
                })));

        await _strategy.OrchestrateAsync(
            CreateMessage(new { labels = new[] { "agent:BACKEND" } }),
            _context,
            TestContext.Current.CancellationToken);

        await _context.Received(1).SendAsync(
            Arg.Is<Message>(m => m.To == target),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrchestrateAsync_MatchedPathNotInMembers_DropsMessage()
    {
        _context.Members.Returns([new Address("agent", "qa-engineer")]);
        _policyRepository
            .GetAsync("engineering-team", Arg.Any<CancellationToken>())
            .Returns(new UnitPolicy(LabelRouting: new LabelRoutingPolicy(
                TriggerLabels: new Dictionary<string, string>
                {
                    ["agent:backend"] = "backend-engineer", // not a member
                })));

        var result = await _strategy.OrchestrateAsync(
            CreateMessage(new { labels = new[] { "agent:backend" } }),
            _context,
            TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        await _context.DidNotReceive().SendAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrchestrateAsync_FirstMatchingLabelInPayloadOrderWins()
    {
        var backend = new Address("agent", "backend-engineer");
        var qa = new Address("agent", "qa-engineer");
        _context.Members.Returns([backend, qa]);
        _policyRepository
            .GetAsync("engineering-team", Arg.Any<CancellationToken>())
            .Returns(new UnitPolicy(LabelRouting: new LabelRoutingPolicy(
                TriggerLabels: new Dictionary<string, string>
                {
                    ["agent:backend"] = "backend-engineer",
                    ["agent:qa"] = "qa-engineer",
                })));

        await _strategy.OrchestrateAsync(
            CreateMessage(new { labels = new[] { "agent:qa", "agent:backend" } }),
            _context,
            TestContext.Current.CancellationToken);

        // The qa label appeared first in the payload, so qa-engineer wins —
        // even though backend-engineer was declared first on the policy.
        await _context.Received(1).SendAsync(
            Arg.Is<Message>(m => m.To == qa),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ExtractLabels_ReturnsEmpty_WhenPayloadIsNotObject()
    {
        var payload = JsonSerializer.SerializeToElement("not an object");
        LabelRoutedOrchestrationStrategy.ExtractLabels(payload).ShouldBeEmpty();
    }

    [Fact]
    public void ExtractLabels_ReturnsEmpty_WhenLabelsMissing()
    {
        var payload = JsonSerializer.SerializeToElement(new { action = "opened" });
        LabelRoutedOrchestrationStrategy.ExtractLabels(payload).ShouldBeEmpty();
    }

    [Fact]
    public void ExtractLabels_StringArray_ExtractsNames()
    {
        var payload = JsonSerializer.SerializeToElement(new { labels = new[] { "a", "b" } });
        LabelRoutedOrchestrationStrategy.ExtractLabels(payload).ShouldBe(new[] { "a", "b" });
    }

    [Fact]
    public void ExtractLabels_ObjectArrayWithNameField_ExtractsNames()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            labels = new[]
            {
                new { name = "a" },
                new { name = "b" },
            },
        });
        LabelRoutedOrchestrationStrategy.ExtractLabels(payload).ShouldBe(new[] { "a", "b" });
    }

    [Fact]
    public void ExtractLabels_MixedValues_SkipsUnrecognised()
    {
        // An array mixing valid and invalid entries is tolerated — unrecognised
        // entries are dropped so one malformed label does not nuke the whole
        // routing decision.
        var rawJson = "{\"labels\":[\"ok\",{\"name\":\"also-ok\"},123,{\"notName\":\"ignored\"}]}";
        using var doc = JsonDocument.Parse(rawJson);
        LabelRoutedOrchestrationStrategy.ExtractLabels(doc.RootElement.Clone())
            .ShouldBe(new[] { "ok", "also-ok" });
    }

    [Fact]
    public void FindMatch_ReturnsFirstPayloadLabelHit()
    {
        var (label, path) = LabelRoutedOrchestrationStrategy.FindMatch(
            payloadLabels: new[] { "bug", "agent:backend" },
            triggerLabels: new Dictionary<string, string>
            {
                ["agent:backend"] = "backend-engineer",
                ["agent:qa"] = "qa-engineer",
            });

        label.ShouldBe("agent:backend");
        path.ShouldBe("backend-engineer");
    }

    [Fact]
    public void FindMatch_ReturnsNull_WhenNoPayloadLabelInMap()
    {
        var (label, path) = LabelRoutedOrchestrationStrategy.FindMatch(
            payloadLabels: new[] { "bug" },
            triggerLabels: new Dictionary<string, string>
            {
                ["agent:backend"] = "backend-engineer",
            });

        label.ShouldBeNull();
        path.ShouldBeNull();
    }

    [Fact]
    public void ResolveMember_MatchesOnPathCaseInsensitively()
    {
        var agent = new Address("agent", "Backend-Engineer");
        var result = LabelRoutedOrchestrationStrategy.ResolveMember("backend-engineer", [agent]);
        result.ShouldBe(agent);
    }

    [Fact]
    public void ResolveMember_ReturnsNull_WhenPathNotInMembers()
    {
        var result = LabelRoutedOrchestrationStrategy.ResolveMember(
            "ghost",
            [new Address("agent", "backend-engineer")]);
        result.ShouldBeNull();
    }
}