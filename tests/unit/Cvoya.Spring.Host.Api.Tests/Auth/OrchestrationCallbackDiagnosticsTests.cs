// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Tests.Auth;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Runtime;
using Cvoya.Spring.Dispatcher.Auth;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Pins <see cref="OrchestrationCallbackDiagnostics"/> (#2582): a
/// callback-token rejection emits exactly one
/// <see cref="ActivityEventType.ErrorOccurred"/> activity carrying the
/// subject and the structured <see cref="CallbackTokenValidationReason"/>,
/// and recovers a best-effort subject from a rejected token's unverified
/// address claim.
/// </summary>
public class OrchestrationCallbackDiagnosticsTests
{
    private static OrchestrationCallbackDiagnostics CreateDiagnostics(IActivityEventBus bus) =>
        new(bus, NullLogger<OrchestrationCallbackDiagnostics>.Instance);

    private static string MakeJwtWithAddress(string address)
    {
        var handler = new JwtSecurityTokenHandler();
        var identity = new ClaimsIdentity(
            new[] { new Claim(CallbackTokenClaimNames.AgentAddress, address) });
        var token = handler.CreateJwtSecurityToken(subject: identity);
        return handler.WriteToken(token);
    }

    [Fact]
    public async Task RecordRejection_EmitsSingleErrorOccurredWithReason()
    {
        var bus = Substitute.For<IActivityEventBus>();
        ActivityEvent? captured = null;
        await bus.PublishAsync(Arg.Do<ActivityEvent>(e => captured = e), Arg.Any<CancellationToken>());

        var diagnostics = CreateDiagnostics(bus);
        var ex = new CallbackTokenValidationException(
            CallbackTokenValidationReason.Expired, "Callback token has expired.");

        await diagnostics.RecordRejectionAsync(ex, rejectedToken: null, TestContext.Current.CancellationToken);

        await bus.Received(1).PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>());
        captured.ShouldNotBeNull();
        captured!.EventType.ShouldBe(ActivityEventType.ErrorOccurred);
        captured.Severity.ShouldBe(ActivitySeverity.Warning);
        captured.Details!.Value.GetProperty("reason").GetString()
            .ShouldBe(CallbackTokenValidationReason.Expired.ToString());
    }

    [Fact]
    public async Task RecordRejection_RecoversSubjectFromRejectedTokenAddressClaim()
    {
        var bus = Substitute.For<IActivityEventBus>();
        ActivityEvent? captured = null;
        await bus.PublishAsync(Arg.Do<ActivityEvent>(e => captured = e), Arg.Any<CancellationToken>());

        var subjectId = Guid.NewGuid();
        var subject = new Address(Address.UnitScheme, subjectId);
        var token = MakeJwtWithAddress(subject.ToString());

        var diagnostics = CreateDiagnostics(bus);
        var ex = new CallbackTokenValidationException(
            CallbackTokenValidationReason.Expired, "expired");

        await diagnostics.RecordRejectionAsync(ex, token, TestContext.Current.CancellationToken);

        // Even though the token did not validate, the unverified sv_addr
        // claim is the most precise subject available for the activity.
        captured!.Source.Id.ShouldBe(subjectId);
        captured.Source.Scheme.ShouldBe(Address.UnitScheme);
    }

    [Fact]
    public async Task RecordRejection_MalformedToken_UsesSentinelSubject()
    {
        var bus = Substitute.For<IActivityEventBus>();
        ActivityEvent? captured = null;
        await bus.PublishAsync(Arg.Do<ActivityEvent>(e => captured = e), Arg.Any<CancellationToken>());

        var diagnostics = CreateDiagnostics(bus);
        var ex = new CallbackTokenValidationException(
            CallbackTokenValidationReason.Malformed, "not a jwt");

        await diagnostics.RecordRejectionAsync(ex, rejectedToken: "garbage", TestContext.Current.CancellationToken);

        // No usable subject — the diagnostic still emits with a well-formed
        // all-zero unit sentinel Source.
        captured!.Source.Id.ShouldBe(Guid.Empty);
        captured.Source.Scheme.ShouldBe(Address.UnitScheme);
    }

    [Fact]
    public async Task RecordRejection_BusFailure_DoesNotThrow()
    {
        var bus = Substitute.For<IActivityEventBus>();
        bus.PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("bus down"));

        var diagnostics = CreateDiagnostics(bus);
        var ex = new CallbackTokenValidationException(
            CallbackTokenValidationReason.SignatureInvalid, "bad sig");

        // A bus failure must never turn a 401 into a 500.
        await Should.NotThrowAsync(async () =>
            await diagnostics.RecordRejectionAsync(
                ex, rejectedToken: null, TestContext.Current.CancellationToken));
    }
}
