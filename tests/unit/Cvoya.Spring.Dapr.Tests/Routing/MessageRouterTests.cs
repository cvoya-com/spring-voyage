// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Routing;

using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Tenancy;
using Cvoya.Spring.Dapr.Threads;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="MessageRouter"/>.
///
/// Post #1629: every Address path is a no-dash 32-hex Guid; tests use named
/// Guid constants for the actor identities.
/// </summary>
public class MessageRouterTests
{
    private static readonly Guid AdaId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid BobId = new("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid SenderId = new("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid UnitOneId = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid HumanLocalId = new("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid HumanAuthorisedId = new("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid HumanUnauthorisedId = new("cccccccc-0000-0000-0000-000000000003");

    private static string Hex(Guid g) => g.ToString("N");

    private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
    private readonly IAgentProxyResolver _agentProxyResolver = Substitute.For<IAgentProxyResolver>();
    private readonly IPermissionService _permissionService = Substitute.For<IPermissionService>();
    private readonly ILoggerFactory _loggerFactory;
    private readonly MessageRouter _router;

    public MessageRouterTests()
    {
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _router = new MessageRouter(
            _directoryService,
            _agentProxyResolver,
            _permissionService,
            _loggerFactory,
            NullMessageWriterScopeFactory.Create());
    }

    [Fact]
    public async Task RouteAsync_path_address_resolves_and_delivers_message()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("agent", AdaId);
        var entry = new DirectoryEntry(destination, AdaId, "Ada", "Engineer", null, DateTimeOffset.UtcNow);
        var message = CreateMessage(destination);
        var expectedResponse = CreateResponse(message);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>())
            .Returns(entry);

        var actorProxy = Substitute.For<IAgent>();
        actorProxy.ReceiveAsync(message, Arg.Any<CancellationToken>())
            .Returns(expectedResponse);

        _agentProxyResolver.Resolve("agent", Hex(AdaId)).Returns(actorProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expectedResponse);
    }

    [Fact]
    public async Task RouteAsync_unknown_address_returns_AddressNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("agent", Guid.NewGuid());
        var message = CreateMessage(destination);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("ADDRESS_NOT_FOUND");
    }

    [Fact]
    public async Task RouteAsync_multicast_role_address_fans_out_to_multiple_actors()
    {
        var ct = TestContext.Current.CancellationToken;
        var roleAddress = new Address("role", Guid.NewGuid()); // Role address: scheme triggers multicast.
        var message = CreateMessage(roleAddress);

        var entry1 = new DirectoryEntry(
            new Address("agent", AdaId), AdaId, "Ada", "Engineer", "backend-engineer", DateTimeOffset.UtcNow);
        var entry2 = new DirectoryEntry(
            new Address("agent", BobId), BobId, "Bob", "Engineer", "backend-engineer", DateTimeOffset.UtcNow);

        _directoryService.ResolveByRoleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([entry1, entry2]);

        var proxy1 = Substitute.For<IAgent>();
        proxy1.ReceiveAsync(message, Arg.Any<CancellationToken>())
            .Returns(CreateResponse(message, "response-1"));

        var proxy2 = Substitute.For<IAgent>();
        proxy2.ReceiveAsync(message, Arg.Any<CancellationToken>())
            .Returns(CreateResponse(message, "response-2"));

        _agentProxyResolver.Resolve("agent", Hex(AdaId)).Returns(proxy1);
        _agentProxyResolver.Resolve("agent", Hex(BobId)).Returns(proxy2);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
    }

    [Fact]
    public async Task RouteAsync_delivery_failure_returns_DeliveryFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("agent", AdaId);
        var entry = new DirectoryEntry(destination, AdaId, "Ada", "Engineer", null, DateTimeOffset.UtcNow);
        var message = CreateMessage(destination);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>())
            .Returns(entry);

        var actorProxy = Substitute.For<IAgent>();
        actorProxy.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Actor unavailable"));

        _agentProxyResolver.Resolve("agent", Hex(AdaId)).Returns(actorProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("DELIVERY_FAILED");
    }

    [Fact]
    public async Task RouteAsync_caller_validation_exception_returns_CallerValidation()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("agent", AdaId);
        var entry = new DirectoryEntry(destination, AdaId, "Ada", "Engineer", null, DateTimeOffset.UtcNow);
        var message = CreateMessage(destination);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>())
            .Returns(entry);

        var actorProxy = Substitute.For<IAgent>();
        actorProxy.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Throws(new CallerValidationException(
                CallerValidationCodes.MissingThreadId,
                "Domain messages must have a ThreadId"));

        _agentProxyResolver.Resolve("agent", Hex(AdaId)).Returns(actorProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("CALLER_VALIDATION");
        result.Error.DetailCode.ShouldBe(CallerValidationCodes.MissingThreadId);
        result.Error.Detail.ShouldBe("Domain messages must have a ThreadId");
    }

    [Fact]
    public async Task RouteAsync_caller_validation_encoded_in_message_survives_remoting()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("agent", AdaId);
        var entry = new DirectoryEntry(destination, AdaId, "Ada", "Engineer", null, DateTimeOffset.UtcNow);
        var message = CreateMessage(destination);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>())
            .Returns(entry);

        var encodedMessage = new CallerValidationException(
            CallerValidationCodes.UnknownMessageType,
            "Unknown message type: Amendment").Message;

        var actorProxy = Substitute.For<IAgent>();
        actorProxy.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException(encodedMessage));

        _agentProxyResolver.Resolve("agent", Hex(AdaId)).Returns(actorProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("CALLER_VALIDATION");
        result.Error.DetailCode.ShouldBe(CallerValidationCodes.UnknownMessageType);
        result.Error.Detail.ShouldBe("Unknown message type: Amendment");
    }

    // --- Permission Check Tests ---

    [Fact]
    public async Task RouteAsync_HumanToUnitWithPermission_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("unit", UnitOneId);
        var entry = new DirectoryEntry(destination, UnitOneId, "Engineering", "Team", null, DateTimeOffset.UtcNow);
        var message = CreateMessageFromHuman(destination, HumanAuthorisedId);
        var expectedResponse = CreateResponse(message);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>()).Returns(entry);
        _permissionService.ResolveEffectivePermissionAsync(Hex(HumanAuthorisedId), Hex(UnitOneId), Arg.Any<CancellationToken>())
            .Returns(PermissionLevel.Operator);

        var unitProxy = Substitute.For<IAgent>();
        unitProxy.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns(expectedResponse);

        _agentProxyResolver.Resolve("unit", Hex(UnitOneId)).Returns(unitProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task RouteAsync_HumanToUnitWithoutPermission_ReturnsPermissionDenied()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("unit", UnitOneId);
        var entry = new DirectoryEntry(destination, UnitOneId, "Engineering", "Team", null, DateTimeOffset.UtcNow);
        var message = CreateMessageFromHuman(destination, HumanUnauthorisedId);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>()).Returns(entry);
        _permissionService.ResolveEffectivePermissionAsync(Hex(HumanUnauthorisedId), Hex(UnitOneId), Arg.Any<CancellationToken>())
            .Returns((PermissionLevel?)null);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("PERMISSION_DENIED");
    }

    // #1037: human:// addresses must resolve directly to their actor id
    // without a directory lookup.
    [Fact]
    public async Task RouteAsync_HumanDestination_BypassesDirectoryAndDelivers()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("human", HumanLocalId);
        var message = CreateMessage(destination);
        var expectedResponse = CreateResponse(message);

        // Explicitly: no directory entry registered for the human address.
        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var humanProxy = Substitute.For<IAgent>();
        humanProxy.ReceiveAsync(message, Arg.Any<CancellationToken>())
            .Returns(expectedResponse);

        _agentProxyResolver.Resolve("human", Hex(HumanLocalId)).Returns(humanProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expectedResponse);

        // Directory service should NOT have been consulted for human addresses.
        await _directoryService.DidNotReceive().ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RouteAsync_AgentToUnit_SkipsPermissionCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("unit", UnitOneId);
        var entry = new DirectoryEntry(destination, UnitOneId, "Engineering", "Team", null, DateTimeOffset.UtcNow);
        var message = CreateMessage(destination); // From agent, not human
        var expectedResponse = CreateResponse(message);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>()).Returns(entry);

        var unitProxy = Substitute.For<IAgent>();
        unitProxy.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns(expectedResponse);

        _agentProxyResolver.Resolve("unit", Hex(UnitOneId)).Returns(unitProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeTrue();
        // Permission service should NOT have been called for agent-to-unit routing.
        await _permissionService.DidNotReceive().ResolveEffectivePermissionAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RouteAsync_DomainMessage_PersistsToMessagesTableViaWriter()
    {
        // #2053: every Domain message must produce a row in the messages
        // table at dispatch time. The router opens a scope per dispatch and
        // resolves IMessageWriter from it; this test wires a real DI
        // container with the EF writer and an in-memory DbContext to
        // verify the dispatch-time write path end-to-end.
        var ct = TestContext.Current.CancellationToken;
        var tenantId = new Guid("11111111-2222-3333-4444-000000000099");

        var services = new ServiceCollection();
        services.AddLogging();
        var dbName = $"router-{Guid.NewGuid()}";
        services.AddDbContext<SpringDbContext>(opts => opts.UseInMemoryDatabase(dbName));
        services.AddSingleton<ITenantContext>(new StaticTenantContext(tenantId));
        // #2533: EfMessageWriter now snapshots participant display names
        // via IParticipantDisplayNameResolver. The router-end-to-end test
        // doesn't care about snapshot capture — wire a no-op resolver that
        // always reports a fallback so the writer skips the upsert path.
        services.AddScoped<IParticipantDisplayNameResolver>(_ => new FallbackOnlyResolver());
        services.AddScoped<IMessageWriter, EfMessageWriter>();
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        // Pre-allocate a thread row so the FK insert in EfMessageWriter has
        // a valid principal — same pre-condition the API path establishes
        // through IThreadRegistry.GetOrCreateAsync.
        var threadId = Guid.NewGuid();
        using (var seedScope = scopeFactory.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<SpringDbContext>();
            seedDb.Threads.Add(new Cvoya.Spring.Dapr.Data.Entities.ThreadEntity
            {
                Id = threadId,
                TenantId = tenantId,
                ParticipantKey = "agent:" + Hex(SenderId) + "|agent:" + Hex(AdaId),
                Participants = "[]",
                CreatedAt = DateTimeOffset.UtcNow,
                LastActivityAt = DateTimeOffset.UtcNow,
            });
            await seedDb.SaveChangesAsync(ct);
        }

        var destination = new Address("agent", AdaId);
        var entry = new DirectoryEntry(destination, AdaId, "Ada", "Engineer", null, DateTimeOffset.UtcNow);
        var message = new Message(
            Guid.NewGuid(),
            new Address("agent", SenderId),
            destination,
            MessageType.Domain,
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(threadId),
            JsonSerializer.SerializeToElement("hello via dispatch"),
            DateTimeOffset.UtcNow);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>())
            .Returns(entry);

        var actorProxy = Substitute.For<IAgent>();
        actorProxy.ReceiveAsync(message, Arg.Any<CancellationToken>())
            .Returns(CreateResponse(message));
        _agentProxyResolver.Resolve("agent", Hex(AdaId)).Returns(actorProxy);

        var routerWithScope = new MessageRouter(
            _directoryService, _agentProxyResolver, _permissionService, _loggerFactory, scopeFactory);

        var result = await routerWithScope.RouteAsync(message, ct);
        result.IsSuccess.ShouldBeTrue();

        using var verifyScope = scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await verifyDb.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == message.Id, ct);
        row.ShouldNotBeNull();
        row!.ThreadId.ShouldBe(threadId);
        row.SenderId.ShouldBe(SenderId);
        row.RecipientId.ShouldBe(AdaId);
        row.Body.ShouldBe("hello via dispatch");
    }

    [Fact]
    public async Task RouteAsync_NonDomainMessage_DoesNotPersistToMessagesTable()
    {
        // Control messages (HealthCheck / Cancel / StatusQuery) are runtime-
        // only and never make it to the messages table.
        var ct = TestContext.Current.CancellationToken;
        var tenantId = new Guid("11111111-2222-3333-4444-000000000098");

        var services = new ServiceCollection();
        services.AddLogging();
        var dbName = $"router-{Guid.NewGuid()}";
        services.AddDbContext<SpringDbContext>(opts => opts.UseInMemoryDatabase(dbName));
        services.AddSingleton<ITenantContext>(new StaticTenantContext(tenantId));
        // #2533: EfMessageWriter now snapshots participant display names
        // via IParticipantDisplayNameResolver. The router-end-to-end test
        // doesn't care about snapshot capture — wire a no-op resolver that
        // always reports a fallback so the writer skips the upsert path.
        services.AddScoped<IParticipantDisplayNameResolver>(_ => new FallbackOnlyResolver());
        services.AddScoped<IMessageWriter, EfMessageWriter>();
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var destination = new Address("agent", AdaId);
        var entry = new DirectoryEntry(destination, AdaId, "Ada", "Engineer", null, DateTimeOffset.UtcNow);
        var control = new Message(
            Guid.NewGuid(),
            new Address("agent", SenderId),
            destination,
            MessageType.HealthCheck,
            null,
            default,
            DateTimeOffset.UtcNow);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>())
            .Returns(entry);
        var actorProxy = Substitute.For<IAgent>();
        actorProxy.ReceiveAsync(control, Arg.Any<CancellationToken>()).Returns(CreateResponse(control));
        _agentProxyResolver.Resolve("agent", Hex(AdaId)).Returns(actorProxy);

        var routerWithScope = new MessageRouter(
            _directoryService, _agentProxyResolver, _permissionService, _loggerFactory, scopeFactory);

        var result = await routerWithScope.RouteAsync(control, ct);
        result.IsSuccess.ShouldBeTrue();

        using var verifyScope = scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        (await verifyDb.Messages.AsNoTracking().AnyAsync(ct)).ShouldBeFalse();
    }

    private static Message CreateMessageFromHuman(Address to, Guid humanId) =>
        new(
            Guid.NewGuid(),
            new Address("human", humanId),
            to,
            MessageType.Domain,
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Content = "hello" }),
            DateTimeOffset.UtcNow);

    private static Message CreateMessage(Address to) =>
        new(
            Guid.NewGuid(),
            new Address("agent", SenderId),
            to,
            MessageType.Domain,
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Content = "hello" }),
            DateTimeOffset.UtcNow);

    private static Message CreateResponse(Message original, string? label = null) =>
        new(
            Guid.NewGuid(),
            original.To,
            original.From,
            MessageType.Domain,
            original.ThreadId,
            JsonSerializer.SerializeToElement(new { Acknowledged = true, Label = label }),
            DateTimeOffset.UtcNow);

    /// <summary>
    /// Test-double <see cref="IParticipantDisplayNameResolver"/> that
    /// always reports the per-scheme fallback. The
    /// <see cref="EfMessageWriter"/> snapshot path treats this as "no
    /// real name to capture" and skips the upsert, which is what these
    /// router-end-to-end tests need.
    /// </summary>
    private sealed class FallbackOnlyResolver : IParticipantDisplayNameResolver
    {
        public ValueTask<string> ResolveAsync(string address, CancellationToken cancellationToken = default)
            => ValueTask.FromResult("an actor");

        public ValueTask<ParticipantDisplayName> ResolveStatusAsync(string address, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ParticipantDisplayName("an actor", IsFallback: true));
    }
}
