// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Messaging;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Skills;

using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for the <see cref="SvMessagingSkillRegistry"/> tool-boundary
/// validation introduced in #2740: connector and other non-routable
/// recipient kinds are rejected synchronously by the registry's
/// argument-parsing path, so the calling model sees a validation-class
/// tool error before any tenant / scope resolution happens.
/// </summary>
/// <remarks>
/// The downstream guards in
/// <see cref="MessageDeliveryService.EnsureCanReceive"/> and
/// <see cref="MessagingToolHandlers"/> remain as defence-in-depth on the
/// delivery path; this suite focuses on the boundary behaviour the agent
/// observes directly. The full-stack multi-recipient happy-path is
/// covered by <c>MessagingToolHandlersTests</c>.
/// </remarks>
public class SvMessagingSkillRegistryTests
{
    private static readonly Guid CallerId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ThreadId = new("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid ConnectorId = new("dddddddd-0000-0000-0000-000000000001");

    private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

    public SvMessagingSkillRegistryTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
    }

    private SvMessagingSkillRegistry CreateRegistry()
    {
        // The handlers, delivery service, and registry collaborators are
        // wired with no-op mocks because the #2740 boundary check sits
        // entirely above them — a rejected call returns before any of
        // these are touched. A non-rejected call would reach them, but
        // we exercise the happy path through MessagingToolHandlersTests.
        var deliveryService = new MessageDeliveryService(
            Substitute.For<IAgentProxyResolver>(),
            Substitute.For<IMessageTenantResolver>(),
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<MessageDeliveryService>>(),
            Options.Create(new MessageDeliveryOptions()));

        var handlers = new MessagingToolHandlers(
            deliveryService,
            Substitute.For<IUnitMemberGraphStore>(),
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<IActivityEventBus>(),
            Substitute.For<ILogger<MessagingToolHandlers>>());

        return new SvMessagingSkillRegistry(handlers, _tenantContext, _loggerFactory);
    }

    private static ToolCallContext AgentContext() =>
        new(
            CallerId: GuidFormatter.Format(CallerId),
            CallerKind: Address.AgentScheme,
            ThreadId: GuidFormatter.Format(ThreadId));

    private static JsonElement Args(params string[] recipients)
    {
        return JsonSerializer.SerializeToElement(new
        {
            recipients,
            message = "hi",
        });
    }

    [Theory]
    [InlineData(SvMessagingSkillRegistry.SendTool)]
    [InlineData(SvMessagingSkillRegistry.MulticastTool)]
    public async Task ConnectorRecipient_IsRejectedSynchronouslyAtTheToolBoundary(string toolName)
    {
        var registry = CreateRegistry();
        var connectorAddress = $"{Address.ConnectorScheme}:{GuidFormatter.Format(ConnectorId)}";

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(
                toolName,
                Args(connectorAddress),
                AgentContext(),
                TestContext.Current.CancellationToken));

        // The error message names the tool, the failing address, and the
        // UnroutableTarget reject code so any caller-side handling that
        // recognises the downstream MessageDeliveryException code keeps
        // working at the boundary.
        ex.Message.ShouldContain(toolName);
        ex.Message.ShouldContain(connectorAddress);
        ex.Message.ShouldContain("UnroutableTarget");
        ex.Message.ShouldContain(Address.AgentScheme);
        ex.Message.ShouldContain(Address.UnitScheme);
        ex.Message.ShouldContain(Address.HumanScheme);
    }

    [Theory]
    [InlineData(SvMessagingSkillRegistry.SendTool)]
    [InlineData(SvMessagingSkillRegistry.MulticastTool)]
    public async Task TenantUserRecipient_IsRejectedAsNonRoutable(string toolName)
    {
        // The tenant-user scheme is the platform's authenticated principal
        // (ADR-0047), not a messaging recipient. The boundary guard
        // rejects it with the same UnroutableTarget framing as connector
        // addresses so future non-routable schemes inherit the same
        // failure shape without adding scheme-specific branches.
        var registry = CreateRegistry();
        var tenantUserAddress = $"{Address.TenantUserScheme}:{GuidFormatter.Format(ConnectorId)}";

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(
                toolName,
                Args(tenantUserAddress),
                AgentContext(),
                TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(tenantUserAddress);
        ex.Message.ShouldContain("UnroutableTarget");
    }

    [Fact]
    public async Task ConnectorRecipient_InMixedList_RejectsTheWholeCall()
    {
        // A single connector address in an otherwise-routable list still
        // fails the boundary guard: every recipient must pass the
        // routable-scheme check, so the model gets a clear error pointing
        // at the offending address rather than a partial delivery.
        var registry = CreateRegistry();
        var routable = $"{Address.AgentScheme}:{GuidFormatter.Format(Guid.NewGuid())}";
        var connector = $"{Address.ConnectorScheme}:{GuidFormatter.Format(ConnectorId)}";

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(
                SvMessagingSkillRegistry.SendTool,
                Args(routable, connector),
                AgentContext(),
                TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(connector);
        ex.Message.ShouldContain("UnroutableTarget");
    }

    [Fact]
    public void SendToolDescription_NamesValidRecipientKindsAndConnectorRejection()
    {
        // The schema description is what the agent reads via
        // sv.tools.list("messaging"). Per #2740 it names the valid
        // recipient kinds and the UnroutableTarget rejection so a runtime
        // discovering the tool has the contract without round-tripping
        // through the platform-instructions section.
        var registry = CreateRegistry();
        var sendDef = registry.GetToolDefinitions()
            .Single(t => t.Name == SvMessagingSkillRegistry.SendTool);

        sendDef.Description.ShouldContain("humans, agents, and units");
        sendDef.Description.ShouldContain("UnroutableTarget");
        sendDef.Description.ShouldNotContain("recipients' replies");
        sendDef.Description.ShouldNotContain("MessageSent activity");
    }

    [Fact]
    public void MulticastToolDescription_NamesValidRecipientKindsAndConnectorRejection()
    {
        var registry = CreateRegistry();
        var multicastDef = registry.GetToolDefinitions()
            .Single(t => t.Name == SvMessagingSkillRegistry.MulticastTool);

        multicastDef.Description.ShouldContain("humans, agents, and units");
        multicastDef.Description.ShouldContain("UnroutableTarget");
        multicastDef.Description.ShouldNotContain("recipients' replies");
        multicastDef.Description.ShouldNotContain("MessageSent activity");
    }

    // ---- #3035: empty-content rejection -------------------------------------
    // The #3034 cascade started with sv.messaging.respond_to fired as a
    // content-less acknowledgement: the tool accepted it and delivered an empty
    // {} a recipient read as "didn't send". The content guard now rejects an
    // empty/whitespace message at the tool boundary, synchronously, before any
    // delivery happens — so the calling model sees a retry-guiding tool error.

    private static string ValidRecipient() =>
        $"{Address.AgentScheme}:{GuidFormatter.Format(Guid.NewGuid())}";

    private static JsonElement SendArgs(string messageJson) =>
        JsonDocument.Parse(
            $$"""{ "recipients": ["{{ValidRecipient()}}"], "message": {{messageJson}} }""").RootElement;

    [Theory]
    [InlineData(SvMessagingSkillRegistry.SendTool)]
    [InlineData(SvMessagingSkillRegistry.MulticastTool)]
    public async Task EmptyOrMissingMessage_IsRejectedWithRetryGuidingError(string toolName)
    {
        var registry = CreateRegistry();
        var recipient = ValidRecipient();

        // message omitted entirely → the extractor would yield {} and deliver
        // an empty envelope. Reject it instead.
        var args = JsonDocument.Parse($$"""{ "recipients": ["{{recipient}}"] }""").RootElement;

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(toolName, args, AgentContext(), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(toolName);
        ex.Message.ShouldContain("non-empty message");
    }

    [Theory]
    [InlineData("\"\"")]                       // empty string
    [InlineData("\"   \"")]                     // whitespace-only string
    [InlineData("{}")]                           // empty object
    [InlineData("{ \"content\": \"\" }")]       // empty content field
    [InlineData("{ \"content\": \"   \" }")]    // whitespace-only content field
    [InlineData("{ \"text\": \"  \" }")]        // whitespace-only text field
    [InlineData("null")]                          // JSON null
    [InlineData("123")]                           // a non-string/non-object scalar (collapses to {})
    public async Task Send_ContentLessMessage_IsRejected(string messageJson)
    {
        var registry = CreateRegistry();
        var args = SendArgs(messageJson);

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(
                SvMessagingSkillRegistry.SendTool, args, AgentContext(), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("non-empty message");
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("{}")]
    [InlineData("{ \"content\": \"\" }")]
    public async Task RespondTo_ContentLessMessage_IsRejected(string messageJson)
    {
        // respond_to was the #3034 culprit. A valid message_id is supplied so
        // the call clears the message_id gate and reaches the content guard.
        var registry = CreateRegistry();
        var args = JsonDocument.Parse(
            $$"""{ "message_id": "{{GuidFormatter.Format(Guid.NewGuid())}}", "message": {{messageJson}} }""")
            .RootElement;

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(
                SvMessagingSkillRegistry.RespondToTool, args, AgentContext(), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(SvMessagingSkillRegistry.RespondToTool);
        ex.Message.ShouldContain("non-empty message");
    }

    [Fact]
    public async Task Send_StructuredObjectPayload_PassesContentGuard()
    {
        // A structured object the recipient sees as JSON in the inbound
        // envelope is real content — the empty-content guard must NOT reject it
        // (no behavior change for well-formed sends). The call proceeds past
        // the guard into the delivery layer (exercised by MessagingToolHandlers
        // tests); here we only assert it is not the content-guard rejection.
        var registry = CreateRegistry();
        var args = JsonDocument.Parse(
            $$"""{ "recipients": ["{{ValidRecipient()}}"], "message": { "vote": "A", "weight": 3 } }""")
            .RootElement;

        Exception? thrown = null;
        try
        {
            await registry.InvokeAsync(
                SvMessagingSkillRegistry.SendTool, args, AgentContext(), TestContext.Current.CancellationToken);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        // The only ArgumentException reachable on a valid-recipient path is the
        // content guard; anything else means the structured payload was accepted.
        thrown.ShouldNotBeOfType<ArgumentException>();
    }

    // ---- #3036: retry-guiding exactly-one-of error --------------------------

    [Theory]
    [InlineData(SvMessagingSkillRegistry.SendTool)]
    [InlineData(SvMessagingSkillRegistry.MulticastTool)]
    public async Task NeitherRecipientsNorScope_IsRejectedWithRetryGuidingError(string toolName)
    {
        var registry = CreateRegistry();
        var args = JsonDocument.Parse("""{ "message": "hi" }""").RootElement;

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(toolName, args, AgentContext(), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("EXACTLY ONE");
        ex.Message.ShouldContain("neither");
    }

    [Theory]
    [InlineData(SvMessagingSkillRegistry.SendTool)]
    [InlineData(SvMessagingSkillRegistry.MulticastTool)]
    public async Task BothRecipientsAndScope_IsRejectedWithRetryGuidingError(string toolName)
    {
        var registry = CreateRegistry();
        var args = JsonDocument.Parse(
            $$"""{ "recipients": ["{{ValidRecipient()}}"], "scope": "siblings", "message": "hi" }""")
            .RootElement;

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(toolName, args, AgentContext(), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("EXACTLY ONE");
        ex.Message.ShouldContain("both");
    }

    [Fact]
    public async Task EmptyRecipientsArray_CountsAsNotProvided_AndIsRejected()
    {
        // An empty recipients array names no targets; it must not slip past the
        // exactly-one gate and reach the delivery path with zero recipients.
        var registry = CreateRegistry();
        var args = JsonDocument.Parse("""{ "recipients": [], "message": "hi" }""").RootElement;

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(
                SvMessagingSkillRegistry.SendTool, args, AgentContext(), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("EXACTLY ONE");
    }

    // #3088: a consumer (e.g. the magazine-langgraph orchestrator) stores the
    // id this ack returns and later correlates a reply's `in_reply_to` against
    // it. The inbound envelope renders `message_id` / `in_reply_to` in the
    // canonical no-dash GuidFormatter form, so the ack MUST use the same form —
    // a dashed `Guid.ToString("D")` here made every correlation silently miss.
    [Fact]
    public void SerializeSendResult_EmitsCanonicalNoDashMessageId()
    {
        var id = new Guid("685661b4-04f8-4cdc-b986-3c7a36e689b9");
        var json = SvMessagingSkillRegistry.SerializeSendResult(new SendResult(id, ThreadId, []));

        var messageId = json.GetProperty("messageId").GetString();
        messageId.ShouldBe(GuidFormatter.Format(id));
        messageId.ShouldBe("685661b404f84cdcb9863c7a36e689b9");
        messageId!.ShouldNotContain("-");
    }

    [Fact]
    public void SerializeMulticastResult_EmitsCanonicalNoDashMessageId()
    {
        var id = new Guid("685661b4-04f8-4cdc-b986-3c7a36e689b9");
        var json = SvMessagingSkillRegistry.SerializeMulticastResult(new MulticastResult(id, []));

        var messageId = json.GetProperty("messageId").GetString();
        messageId.ShouldBe(GuidFormatter.Format(id));
        messageId!.ShouldNotContain("-");
    }
}
