// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="MessageRouterSkillInvoker"/> — the default
/// <see cref="ISkillInvoker"/> implementation that translates skill calls
/// into <see cref="Message"/> dispatch via <see cref="IMessageRouter"/>.
/// Covers:
/// <list type="bullet">
///   <item><description>Invocation translates to router dispatch with the
///     target resolved from the catalog, correlation id threaded through,
///     and arguments wrapped in a self-describing envelope.</description></item>
///   <item><description>Invocation-time boundary re-check — a skill hidden
///     from the caller returns <c>SKILL_NOT_FOUND</c> without calling the
///     router.</description></item>
///   <item><description>Routing failures surface through the invoker without
///     leaking <see cref="Message"/> internals.</description></item>
/// </list>
/// </summary>
public class MessageRouterSkillInvokerTests
{
    private readonly IExpertiseSkillCatalog _catalog = Substitute.For<IExpertiseSkillCatalog>();
    private readonly IMessageRouter _router = Substitute.For<IMessageRouter>();
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IExpertiseSearch _search = Substitute.For<IExpertiseSearch>();

    public MessageRouterSkillInvokerTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _search.SearchAsync(Arg.Any<ExpertiseSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ExpertiseSearchResult(
                Array.Empty<ExpertiseSearchHit>(), 0, 50, 0));
    }

    private MessageRouterSkillInvoker CreateInvoker() =>
        new(_catalog, _router, _time, _loggerFactory,
            new DirectorySearchSkillRegistry(_search, _loggerFactory));

    private static ExpertiseSkill MakeSkill(string name, Address target)
    {
        var domain = new ExpertiseDomain(name, $"{name} desc", ExpertiseLevel.Advanced, "{\"type\":\"object\"}");
        var tool = new ToolDefinition(
            ExpertiseSkillNaming.GetSkillName(domain),
            domain.Description,
            ExpertiseSkillNaming.ParseSchemaOrEmpty(domain.InputSchemaJson));
        var entry = new ExpertiseEntry(domain, target, new[] { target });
        return new ExpertiseSkill(tool.Name, tool, target, entry);
    }

    [Fact]
    public async Task InvokeAsync_TranslatesToRouterDispatchWithCorrectTarget()
    {
        var invoker = CreateInvoker();
        var agent = new Address("agent", "ada");
        var skill = MakeSkill("python", agent);

        _catalog
            .ResolveAsync(skill.SkillName, Arg.Any<BoundaryViewContext>(), Arg.Any<CancellationToken>())
            .Returns(skill);

        Message? routedMessage = null;
        _router.RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                routedMessage = ci.ArgAt<Message>(0);
                using var empty = JsonDocument.Parse("{\"ok\":true}");
                var response = routedMessage with
                {
                    Id = Guid.NewGuid(),
                    From = routedMessage.To,
                    To = routedMessage.From,
                    Payload = empty.RootElement.Clone(),
                };
                return Result<Message?, RoutingError>.Success(response);
            });

        using var args = JsonDocument.Parse("{\"query\":\"hello\"}");
        var invocation = new SkillInvocation(
            skill.SkillName,
            args.RootElement.Clone(),
            Caller: new Address("agent", "caller"),
            CorrelationId: "conv-42");

        var result = await invoker.InvokeAsync(invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Payload.GetProperty("ok").GetBoolean().ShouldBeTrue();

        routedMessage.ShouldNotBeNull();
        routedMessage.To.ShouldBe(agent);
        routedMessage.From.Path.ShouldBe("caller");
        routedMessage.Type.ShouldBe(MessageType.Domain);
        routedMessage.ThreadId.ShouldBe("conv-42");
        // Envelope carries skill metadata + original arguments.
        routedMessage.Payload.GetProperty("skill").GetString().ShouldBe(skill.SkillName);
        routedMessage.Payload.GetProperty("expertise").GetString().ShouldBe("python");
        routedMessage.Payload.GetProperty("arguments").GetProperty("query").GetString().ShouldBe("hello");
    }

    [Fact]
    public async Task InvokeAsync_CatalogResolvesNull_DoesNotCallRouter_ReturnsSkillNotFound()
    {
        var invoker = CreateInvoker();

        _catalog
            .ResolveAsync(Arg.Any<string>(), Arg.Any<BoundaryViewContext>(), Arg.Any<CancellationToken>())
            .Returns((ExpertiseSkill?)null);

        using var args = JsonDocument.Parse("{}");
        var invocation = new SkillInvocation("expertise/unknown", args.RootElement.Clone());

        var result = await invoker.InvokeAsync(invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("SKILL_NOT_FOUND");
        await _router.DidNotReceiveWithAnyArgs().RouteAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InvokeAsync_BoundaryHidesSkillFromCaller_ResolvesToSkillNotFound()
    {
        // Defence-in-depth: the catalog's Resolve is already boundary-aware.
        // Simulate a caller-aware resolution where external sees nothing and
        // internal sees the skill — the invoker must pass the right context
        // so the caller's view is enforced at call time, not just at
        // enumeration time.
        var invoker = CreateInvoker();
        var agent = new Address("agent", "ada");
        var skill = MakeSkill("python", agent);

        _catalog
            .ResolveAsync(
                skill.SkillName,
                Arg.Is<BoundaryViewContext>(c => !c.Internal),
                Arg.Any<CancellationToken>())
            .Returns((ExpertiseSkill?)null);
        _catalog
            .ResolveAsync(
                skill.SkillName,
                Arg.Is<BoundaryViewContext>(c => c.Internal),
                Arg.Any<CancellationToken>())
            .Returns(skill);

        using var args = JsonDocument.Parse("{}");
        var externalInvocation = new SkillInvocation(skill.SkillName, args.RootElement.Clone());

        var externalResult = await invoker.InvokeAsync(externalInvocation, TestContext.Current.CancellationToken);

        externalResult.IsSuccess.ShouldBeFalse();
        externalResult.ErrorCode.ShouldBe("SKILL_NOT_FOUND");
        await _router.DidNotReceiveWithAnyArgs().RouteAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InvokeAsync_RoutingFailure_SurfacesErrorCode()
    {
        var invoker = CreateInvoker();
        var skill = MakeSkill("python", new Address("agent", "ada"));
        _catalog.ResolveAsync(skill.SkillName, Arg.Any<BoundaryViewContext>(), Arg.Any<CancellationToken>())
            .Returns(skill);

        _router.RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(Result<Message?, RoutingError>.Failure(
                RoutingError.PermissionDenied(new Address("agent", "ada"))));

        using var args = JsonDocument.Parse("{}");
        var invocation = new SkillInvocation(skill.SkillName, args.RootElement.Clone());

        var result = await invoker.InvokeAsync(invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorCode.ShouldBe("PERMISSION_DENIED");
    }

    [Fact]
    public async Task InvokeAsync_MissingCorrelationId_GeneratesOne()
    {
        var invoker = CreateInvoker();
        var skill = MakeSkill("python", new Address("agent", "ada"));
        _catalog.ResolveAsync(skill.SkillName, Arg.Any<BoundaryViewContext>(), Arg.Any<CancellationToken>())
            .Returns(skill);

        Message? routedMessage = null;
        _router.RouteAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                routedMessage = ci.ArgAt<Message>(0);
                return Result<Message?, RoutingError>.Success((Message?)null);
            });

        using var args = JsonDocument.Parse("{}");
        var invocation = new SkillInvocation(skill.SkillName, args.RootElement.Clone());

        var result = await invoker.InvokeAsync(invocation, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        routedMessage.ShouldNotBeNull();
        routedMessage.ThreadId.ShouldNotBeNullOrWhiteSpace();
    }
}