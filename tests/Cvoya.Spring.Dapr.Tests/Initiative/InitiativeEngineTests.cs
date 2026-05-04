// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Initiative;

using System.Text.Json;

using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Dapr.Initiative;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="InitiativeEngine"/> covering the agent-instructions
/// plumbing introduced by #1617. The engine should pass the caller-supplied
/// instructions through to the screening and reflection cognition contexts and
/// substitute a documented fallback when the caller passes <c>null</c> / empty.
/// </summary>
public class InitiativeEngineTests
{
    private const string AgentId = "agent-under-test";

    private readonly ICognitionProvider _tier1 = Substitute.For<ICognitionProvider>();
    private readonly ICognitionProvider _tier2 = Substitute.For<ICognitionProvider>();
    private readonly IAgentPolicyStore _policyStore = Substitute.For<IAgentPolicyStore>();
    private readonly IInitiativeBudgetTracker _budgetTracker = Substitute.For<IInitiativeBudgetTracker>();
    private readonly ILogger<InitiativeEngine> _logger = Substitute.For<ILogger<InitiativeEngine>>();

    private InitiativeEngine CreateSut()
        => new(_tier1, _tier2, _policyStore, _budgetTracker, _logger);

    private void ArrangePolicy(
        InitiativeLevel level = InitiativeLevel.Autonomous,
        IReadOnlyList<string>? allowedActions = null)
    {
        _policyStore
            .GetPolicyAsync(AgentId, Arg.Any<CancellationToken>())
            .Returns(new InitiativePolicy(
                MaxLevel: level,
                AllowedActions: allowedActions));
    }

    private static IReadOnlyList<JsonElement> Observations(int count)
    {
        var list = new List<JsonElement>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(JsonSerializer.SerializeToElement(new { summary = $"obs-{i}" }));
        }

        return list;
    }

    [Fact]
    public async Task ProcessObservationsAsync_PassesRealInstructionsToScreening()
    {
        ArrangePolicy(allowedActions: ["send-message"]);
        _tier1
            .ScreenAsync(Arg.Any<ScreeningContext>(), Arg.Any<CancellationToken>())
            .Returns(InitiativeDecision.Ignore);

        var sut = CreateSut();

        await sut.ProcessObservationsAsync(
            AgentId,
            Observations(1),
            agentInstructions: "You are a code-review agent.",
            TestContext.Current.CancellationToken);

        await _tier1.Received(1).ScreenAsync(
            Arg.Is<ScreeningContext>(ctx =>
                ctx.AgentInstructions == "You are a code-review agent."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessObservationsAsync_PassesRealInstructionsToReflection()
    {
        ArrangePolicy(allowedActions: ["send-message"]);
        _tier1
            .ScreenAsync(Arg.Any<ScreeningContext>(), Arg.Any<CancellationToken>())
            .Returns(InitiativeDecision.ActImmediately);
        _budgetTracker
            .TryConsumeAsync(AgentId, Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _tier2
            .ReflectAsync(Arg.Any<ReflectionContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReflectionOutcome(false));

        var sut = CreateSut();

        await sut.ProcessObservationsAsync(
            AgentId,
            Observations(1),
            agentInstructions: "You are a code-review agent.",
            TestContext.Current.CancellationToken);

        await _tier2.Received(1).ReflectAsync(
            Arg.Is<ReflectionContext>(ctx =>
                ctx.AgentInstructions == "You are a code-review agent."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessObservationsAsync_NullInstructions_UsesDocumentedFallback()
    {
        ArrangePolicy(allowedActions: ["send-message"]);
        _tier1
            .ScreenAsync(Arg.Any<ScreeningContext>(), Arg.Any<CancellationToken>())
            .Returns(InitiativeDecision.Ignore);

        var sut = CreateSut();

        await sut.ProcessObservationsAsync(
            AgentId,
            Observations(1),
            agentInstructions: null,
            TestContext.Current.CancellationToken);

        await _tier1.Received(1).ScreenAsync(
            Arg.Is<ScreeningContext>(ctx =>
                ctx.AgentInstructions == InitiativeEngine.MissingInstructionsFallback),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessObservationsAsync_WhitespaceInstructions_UsesDocumentedFallback()
    {
        ArrangePolicy(allowedActions: ["send-message"]);
        _tier1
            .ScreenAsync(Arg.Any<ScreeningContext>(), Arg.Any<CancellationToken>())
            .Returns(InitiativeDecision.Ignore);

        var sut = CreateSut();

        await sut.ProcessObservationsAsync(
            AgentId,
            Observations(1),
            agentInstructions: "   ",
            TestContext.Current.CancellationToken);

        await _tier1.Received(1).ScreenAsync(
            Arg.Is<ScreeningContext>(ctx =>
                ctx.AgentInstructions == InitiativeEngine.MissingInstructionsFallback),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessObservationsAsync_FallbackDoesNotMentionAllowedActions()
    {
        // Regression guard: the historical placeholder synthesised an
        // "Allowed actions: ..." string from the policy. The fallback for the
        // missing-instructions case must NOT leak the policy's allow-list as
        // a stand-in role description.
        ArrangePolicy(allowedActions: ["send-message", "create-issue"]);
        _tier1
            .ScreenAsync(Arg.Any<ScreeningContext>(), Arg.Any<CancellationToken>())
            .Returns(InitiativeDecision.Ignore);

        var sut = CreateSut();

        await sut.ProcessObservationsAsync(
            AgentId,
            Observations(1),
            agentInstructions: null,
            TestContext.Current.CancellationToken);

        await _tier1.Received(1).ScreenAsync(
            Arg.Is<ScreeningContext>(ctx =>
                !ctx.AgentInstructions.Contains("Allowed actions:") &&
                !ctx.AgentInstructions.Contains("send-message")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessObservationsAsync_PassiveAgent_DoesNotInvokeCognition()
    {
        ArrangePolicy(level: InitiativeLevel.Passive);

        var sut = CreateSut();

        var result = await sut.ProcessObservationsAsync(
            AgentId,
            Observations(1),
            agentInstructions: "Real instructions.",
            TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        await _tier1.DidNotReceive().ScreenAsync(
            Arg.Any<ScreeningContext>(), Arg.Any<CancellationToken>());
        await _tier2.DidNotReceive().ReflectAsync(
            Arg.Any<ReflectionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessObservationsAsync_EmptyObservations_DoesNotInvokeCognition()
    {
        ArrangePolicy();

        var sut = CreateSut();

        var result = await sut.ProcessObservationsAsync(
            AgentId,
            observations: Array.Empty<JsonElement>(),
            agentInstructions: "Real instructions.",
            TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        await _tier1.DidNotReceive().ScreenAsync(
            Arg.Any<ScreeningContext>(), Arg.Any<CancellationToken>());
    }
}