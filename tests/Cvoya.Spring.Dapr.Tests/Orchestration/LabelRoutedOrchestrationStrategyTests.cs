// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using System.Collections.Generic;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
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
        _context.UnitAddress.Returns(Address.For("unit", TestSlugIds.HexFor("engineering-team")));
    }

    private static Message CreateMessage(object payload) =>
        new(
            Guid.NewGuid(),
            Address.For("connector", TestSlugIds.HexFor("github")),
            Address.For("unit", TestSlugIds.HexFor("engineering-team")),
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
        await _policyRepository.DidNotReceive().GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrchestrateAsync_NoLabelRoutingPolicy_DropsMessage()
    {
        _context.Members.Returns([Address.For("agent", TestSlugIds.HexFor("backend-engineer"))]);
        _policyRepository
            .GetAsync(TestSlugIds.For("engineering-team"), Arg.Any<CancellationToken>())
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
        _context.Members.Returns([Address.For("agent", TestSlugIds.HexFor("backend-engineer"))]);
        _policyRepository
            .GetAsync(TestSlugIds.For("engineering-team"), Arg.Any<CancellationToken>())
            .Returns(new UnitPolicy(LabelRouting: new LabelRoutingPolicy(
                TriggerLabels: new Dictionary<string, string>
                {
                    ["agent:backend"] = TestSlugIds.HexFor("backend-engineer"),
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
        var target = Address.For("agent", TestSlugIds.HexFor("backend-engineer"));
        _context.Members.Returns([
            Address.For("agent", TestSlugIds.HexFor("qa-engineer")),
            target,
        ]);
        _policyRepository
            .GetAsync(TestSlugIds.For("engineering-team"), Arg.Any<CancellationToken>())
            .Returns(new UnitPolicy(LabelRouting: new LabelRoutingPolicy(
                TriggerLabels: new Dictionary<string, string>
                {
                    ["agent:qa"] = TestSlugIds.HexFor("qa-engineer"),
                    ["agent:backend"] = TestSlugIds.HexFor("backend-engineer"),
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
        var target = Address.For("agent", TestSlugIds.HexFor("backend-engineer"));
        _context.Members.Returns([target]);
        _policyRepository
            .GetAsync(TestSlugIds.For("engineering-team"), Arg.Any<CancellationToken>())
            .Returns(new UnitPolicy(LabelRouting: new LabelRoutingPolicy(
                TriggerLabels: new Dictionary<string, string>
                {
                    ["agent:backend"] = TestSlugIds.HexFor("backend-engineer"),
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
        var backendHex = TestSlugIds.HexFor("backend-engineer");
        var target = Address.For("agent", backendHex);
        _context.Members.Returns([target]);
        _policyRepository
            .GetAsync(TestSlugIds.For("engineering-team"), Arg.Any<CancellationToken>())
            .Returns(new UnitPolicy(LabelRouting: new LabelRoutingPolicy(
                TriggerLabels: new Dictionary<string, string>
                {
                    ["Agent:Backend"] = backendHex,
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
        _context.Members.Returns([Address.For("agent", TestSlugIds.HexFor("qa-engineer"))]);
        _policyRepository
            .GetAsync(TestSlugIds.For("engineering-team"), Arg.Any<CancellationToken>())
            .Returns(new UnitPolicy(LabelRouting: new LabelRoutingPolicy(
                TriggerLabels: new Dictionary<string, string>
                {
                    ["agent:backend"] = TestSlugIds.HexFor("backend-engineer"), // not a member
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
        var backend = Address.For("agent", TestSlugIds.HexFor("backend-engineer"));
        var qa = Address.For("agent", TestSlugIds.HexFor("qa-engineer"));
        _context.Members.Returns([backend, qa]);
        _policyRepository
            .GetAsync(TestSlugIds.For("engineering-team"), Arg.Any<CancellationToken>())
            .Returns(new UnitPolicy(LabelRouting: new LabelRoutingPolicy(
                TriggerLabels: new Dictionary<string, string>
                {
                    ["agent:backend"] = TestSlugIds.HexFor("backend-engineer"),
                    ["agent:qa"] = TestSlugIds.HexFor("qa-engineer"),
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
                ["agent:backend"] = TestSlugIds.HexFor("backend-engineer"),
                ["agent:qa"] = TestSlugIds.HexFor("qa-engineer"),
            });

        label.ShouldBe("agent:backend");
        path.ShouldBe(TestSlugIds.HexFor("backend-engineer"));
    }

    [Fact]
    public void FindMatch_ReturnsNull_WhenNoPayloadLabelInMap()
    {
        var (label, path) = LabelRoutedOrchestrationStrategy.FindMatch(
            payloadLabels: new[] { "bug" },
            triggerLabels: new Dictionary<string, string>
            {
                ["agent:backend"] = TestSlugIds.HexFor("backend-engineer"),
            });

        label.ShouldBeNull();
        path.ShouldBeNull();
    }

    [Fact]
    public void ResolveMember_MatchesOnPathCaseInsensitively()
    {
        // Post-#1629 the canonical wire form is lowercase no-dash hex; the
        // case-insensitivity guard still matters because callers may upper-
        // case the hex when copy-pasting from the dashed Guid form.
        var agentPath = TestSlugIds.HexFor("backend-engineer");
        var agent = Address.For("agent", agentPath);
        var result = LabelRoutedOrchestrationStrategy.ResolveMember(agentPath.ToUpperInvariant(), [agent]);
        result.ShouldBe(agent);
    }

    [Fact]
    public void ResolveMember_ReturnsNull_WhenPathNotInMembers()
    {
        var result = LabelRoutedOrchestrationStrategy.ResolveMember(
            "ghost",
            [Address.For("agent", TestSlugIds.HexFor("backend-engineer"))]);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task OrchestrateAsync_SuccessfulAssignment_PublishesLabelRoutedEvent()
    {
        var target = Address.For("agent", TestSlugIds.HexFor("backend-engineer"));
        _context.Members.Returns([target]);
        _policyRepository
            .GetAsync(TestSlugIds.For("engineering-team"), Arg.Any<CancellationToken>())
            .Returns(new UnitPolicy(LabelRouting: new LabelRoutingPolicy(
                TriggerLabels: new Dictionary<string, string>
                {
                    ["agent:backend"] = TestSlugIds.HexFor("backend-engineer"),
                },
                AddOnAssign: new[] { "in-progress" },
                RemoveOnAssign: new[] { "agent:backend" })));

        ActivityEvent? captured = null;
        var bus = Substitute.For<IActivityEventBus>();
        bus.PublishAsync(Arg.Do<ActivityEvent>(e => captured = e), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var strategy = new LabelRoutedOrchestrationStrategy(
            _policyRepository, _loggerFactory, bus);

        var githubPayload = new
        {
            source = "github",
            repository = new { owner = "acme", name = "widgets" },
            issue = new { number = 42 },
            labels = new[] { "agent:backend" },
        };

        await strategy.OrchestrateAsync(
            CreateMessage(githubPayload),
            _context,
            TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.EventType.ShouldBe(ActivityEventType.DecisionMade);
        captured.Details.ShouldNotBeNull();

        var details = captured.Details!.Value;
        details.GetProperty("decision").GetString()
            .ShouldBe(LabelRoutedOrchestrationStrategy.LabelRoutedDecision);
        details.GetProperty("source").GetString().ShouldBe("github");
        details.GetProperty("repository").GetProperty("owner").GetString().ShouldBe("acme");
        details.GetProperty("repository").GetProperty("name").GetString().ShouldBe("widgets");
        details.GetProperty("issue").GetProperty("number").GetInt32().ShouldBe(42);
        details.GetProperty("addOnAssign")[0].GetString().ShouldBe("in-progress");
        details.GetProperty("removeOnAssign")[0].GetString().ShouldBe("agent:backend");
    }

    [Fact]
    public async Task OrchestrateAsync_DroppedMessage_DoesNotPublishEvent()
    {
        _context.Members.Returns([Address.For("agent", TestSlugIds.HexFor("backend-engineer"))]);
        _policyRepository
            .GetAsync(TestSlugIds.For("engineering-team"), Arg.Any<CancellationToken>())
            .Returns(UnitPolicy.Empty);

        var bus = Substitute.For<IActivityEventBus>();
        var strategy = new LabelRoutedOrchestrationStrategy(
            _policyRepository, _loggerFactory, bus);

        await strategy.OrchestrateAsync(
            CreateMessage(new { labels = new[] { "agent:backend" } }),
            _context,
            TestContext.Current.CancellationToken);

        await bus.DidNotReceive().PublishAsync(
            Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrchestrateAsync_BusPublishFailure_DoesNotFaultOrchestration()
    {
        var target = Address.For("agent", TestSlugIds.HexFor("backend-engineer"));
        _context.Members.Returns([target]);
        _policyRepository
            .GetAsync(TestSlugIds.For("engineering-team"), Arg.Any<CancellationToken>())
            .Returns(new UnitPolicy(LabelRouting: new LabelRoutingPolicy(
                TriggerLabels: new Dictionary<string, string>
                {
                    ["agent:backend"] = TestSlugIds.HexFor("backend-engineer"),
                })));

        var bus = Substitute.For<IActivityEventBus>();
        bus.PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("bus is down")));

        var sent = CreateMessage(new { acknowledged = true });
        _context.SendAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(sent);

        var strategy = new LabelRoutedOrchestrationStrategy(
            _policyRepository, _loggerFactory, bus);

        // The publish failure must not surface as an orchestration fault.
        var result = await strategy.OrchestrateAsync(
            CreateMessage(new { labels = new[] { "agent:backend" } }),
            _context,
            TestContext.Current.CancellationToken);

        result.ShouldBe(sent);
    }
}