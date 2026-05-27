// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using Cvoya.Spring.Connector.Slack.Auth.OAuth;
using Cvoya.Spring.Connector.Slack.Configuration;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Covers the Slack OAuth install / disconnect lifecycle (ADR-0061
/// §2.3, §2.5, §7.5):
/// <list type="bullet">
///   <item><description>Happy path — code exchange succeeds, team.info reports a non-Grid workspace, binding persisted.</description></item>
///   <item><description>Grid refusal — team.info reports an enterprise id (or oauth.v2.access carries one) → SlackEnterpriseGridUnsupported; binding NOT persisted.</description></item>
///   <item><description>Re-install with a different team_id is refused (ADR-0061 §2.5).</description></item>
///   <item><description>Disconnect calls auth.revoke + deletes binding + workspace map + secrets.</description></item>
///   <item><description>Invalid / consumed state → InvalidState; nothing persisted.</description></item>
/// </list>
/// </summary>
public class SlackOAuthServiceTests
{
    private static readonly SlackOAuthOptions DefaultOptions = new()
    {
        ClientId = "test-client-id",
        ClientSecret = "test-client-secret",
        SigningSecret = "test-signing-secret",
        RedirectUri = "https://example.test/api/v1/tenant/connectors/slack/oauth/callback",
        Scopes = "chat:write commands",
        StateTtl = TimeSpan.FromMinutes(15),
    };

    private readonly ISlackOAuthStateStore _stateStore = Substitute.For<ISlackOAuthStateStore>();
    private readonly ISlackOAuthHttpClient _http = Substitute.For<ISlackOAuthHttpClient>();
    private readonly ISlackInstallStore _installStore = Substitute.For<ISlackInstallStore>();

    [Fact]
    public async Task BeginAuthorizationAsync_PersistsStateAndReturnsAuthorizeUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = CreateSut();

        var result = await sut.BeginAuthorizationAsync(clientState: null, ct);

        result.AuthorizeUrl.ShouldStartWith("https://slack.com/oauth/v2/authorize?");
        result.AuthorizeUrl.ShouldContain("client_id=test-client-id");
        result.AuthorizeUrl.ShouldContain($"state={Uri.EscapeDataString(result.State)}");
        result.AuthorizeUrl.ShouldContain("scope=");

        await _stateStore.Received(1).SaveAsync(
            Arg.Is<SlackOAuthStateEntry>(s => s.State == result.State),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCallbackAsync_HappyPath_PersistsBinding()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeValidState("state-1");
        ArrangeOAuthExchange(
            teamId: "T1",
            botUserId: "U-bot",
            authedUserId: "U-installer",
            enterpriseId: null);
        ArrangeNonGridTeamInfo("T1");
        ArrangeNoExistingBinding();

        var sut = CreateSut();
        var outcome = await sut.HandleCallbackAsync("code-abc", "state-1", ct);

        outcome.ShouldBeOfType<SlackCallbackOutcome.Success>();
        var success = (SlackCallbackOutcome.Success)outcome;
        success.TeamId.ShouldBe("T1");
        success.BotUserId.ShouldBe("U-bot");
        success.InstallerUserId.ShouldBe("U-installer");

        await _installStore.Received(1).PersistInstallAsync(
            Arg.Is<SlackInstallPayload>(p =>
                p.TeamId == "T1"
                && p.BotUserId == "U-bot"
                && p.InstallerUserId == "U-installer"
                && p.BotAccessToken == "xoxb-test"
                && p.SigningSecret == "test-signing-secret"
                && p.EnterpriseId == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCallbackAsync_TeamInfoReportsEnterprise_RefusesAndDoesNotPersist()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeValidState("state-grid");
        ArrangeOAuthExchange(
            teamId: "T2",
            botUserId: "U-bot",
            authedUserId: "U-installer",
            enterpriseId: null);
        // Slack returns the Grid id via team.info (it could also come
        // through oauth.v2.access — the service must catch either).
        _http.GetTeamInfoAsync("xoxb-test", Arg.Any<CancellationToken>())
            .Returns(new SlackTeamInfo("T2", "Grid Workspace", EnterpriseId: "E123"));

        var sut = CreateSut();
        var outcome = await sut.HandleCallbackAsync("code-abc", "state-grid", ct);

        var grid = outcome.ShouldBeOfType<SlackCallbackOutcome.EnterpriseGridUnsupported>();
        grid.EnterpriseId.ShouldBe("E123");

        await _installStore.DidNotReceive().PersistInstallAsync(
            Arg.Any<SlackInstallPayload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCallbackAsync_OAuthExchangeCarriesEnterpriseId_RefusesAndDoesNotPersist()
    {
        // Defence-in-depth: even if team.info forgot to report the
        // enterprise id, oauth.v2.access's own enterprise.id slot is
        // honoured.
        var ct = TestContext.Current.CancellationToken;
        ArrangeValidState("state-grid-2");
        ArrangeOAuthExchange(
            teamId: "T3",
            botUserId: "U-bot",
            authedUserId: "U-installer",
            enterpriseId: "E456");
        ArrangeNonGridTeamInfo("T3");

        var sut = CreateSut();
        var outcome = await sut.HandleCallbackAsync("code-abc", "state-grid-2", ct);

        var grid = outcome.ShouldBeOfType<SlackCallbackOutcome.EnterpriseGridUnsupported>();
        grid.EnterpriseId.ShouldBe("E456");

        await _installStore.DidNotReceive().PersistInstallAsync(
            Arg.Any<SlackInstallPayload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCallbackAsync_ReinstallDifferentTeamId_Refused()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeValidState("state-reinstall");
        ArrangeOAuthExchange(
            teamId: "T-new",
            botUserId: "U-bot",
            authedUserId: "U-installer",
            enterpriseId: null);
        ArrangeNonGridTeamInfo("T-new");
        _installStore.GetExistingBindingAsync(Arg.Any<CancellationToken>())
            .Returns(new SlackBindingSnapshot(
                TeamId: "T-existing",
                BotTokenSecretName: "slack/T-existing/bot-token",
                SigningSecretSecretName: "slack/T-existing/signing-secret"));

        var sut = CreateSut();
        var outcome = await sut.HandleCallbackAsync("code-abc", "state-reinstall", ct);

        var conflict = outcome.ShouldBeOfType<SlackCallbackOutcome.WorkspaceConflict>();
        conflict.ExpectedTeamId.ShouldBe("T-existing");
        conflict.ReceivedTeamId.ShouldBe("T-new");

        await _installStore.DidNotReceive().PersistInstallAsync(
            Arg.Any<SlackInstallPayload>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCallbackAsync_ReinstallSameTeamId_PersistsRefreshedBinding()
    {
        // Same team_id is a refresh path — re-binding is allowed.
        var ct = TestContext.Current.CancellationToken;
        ArrangeValidState("state-same");
        ArrangeOAuthExchange(
            teamId: "T-same",
            botUserId: "U-bot",
            authedUserId: "U-installer",
            enterpriseId: null);
        ArrangeNonGridTeamInfo("T-same");
        _installStore.GetExistingBindingAsync(Arg.Any<CancellationToken>())
            .Returns(new SlackBindingSnapshot(
                TeamId: "T-same",
                BotTokenSecretName: "slack/T-same/bot-token",
                SigningSecretSecretName: "slack/T-same/signing-secret"));

        var sut = CreateSut();
        var outcome = await sut.HandleCallbackAsync("code-abc", "state-same", ct);

        outcome.ShouldBeOfType<SlackCallbackOutcome.Success>();
        await _installStore.Received(1).PersistInstallAsync(
            Arg.Is<SlackInstallPayload>(p => p.TeamId == "T-same"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCallbackAsync_InvalidState_ReturnsInvalidStateOutcome()
    {
        var ct = TestContext.Current.CancellationToken;
        _stateStore.ConsumeAsync("unknown-state", Arg.Any<CancellationToken>())
            .Returns((SlackOAuthStateEntry?)null);

        var sut = CreateSut();
        var outcome = await sut.HandleCallbackAsync("code-abc", "unknown-state", ct);

        outcome.ShouldBeOfType<SlackCallbackOutcome.InvalidState>();
        await _http.DidNotReceive().ExchangeCodeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleCallbackAsync_OAuthExchangeFails_ReturnsExchangeFailedOutcome()
    {
        var ct = TestContext.Current.CancellationToken;
        ArrangeValidState("state-bad");
        _http.ExchangeCodeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new SlackOAuthExchangeResult(
                Ok: false,
                Error: "invalid_code",
                TeamId: string.Empty,
                TeamName: null,
                BotUserId: string.Empty,
                BotAccessToken: string.Empty,
                AuthedUserId: string.Empty,
                EnterpriseId: null));

        var sut = CreateSut();
        var outcome = await sut.HandleCallbackAsync("code-bad", "state-bad", ct);

        var failure = outcome.ShouldBeOfType<SlackCallbackOutcome.ExchangeFailed>();
        failure.Reason.ShouldContain("invalid_code");
    }

    // ---- Issue #2837: ClientState propagation onto every outcome ----

    [Fact]
    public async Task HandleCallbackAsync_PropagatesConsumedClientStateOnSuccess()
    {
        // Issue #2837: the callback endpoint reads the consumed state's
        // ClientState off the outcome to derive the postMessage
        // targetOrigin. Verify the service threads the value through
        // unchanged so the endpoint doesn't have to re-consume.
        var ct = TestContext.Current.CancellationToken;
        const string clientState = """{"targetOrigin":"https://portal.example"}""";
        _stateStore.ConsumeAsync("state-cs", Arg.Any<CancellationToken>())
            .Returns(new SlackOAuthStateEntry(
                State: "state-cs",
                Scopes: DefaultOptions.Scopes,
                RedirectUri: DefaultOptions.RedirectUri,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10),
                ClientState: clientState));
        ArrangeOAuthExchange(
            teamId: "T-cs", botUserId: "U-bot", authedUserId: "U-installer", enterpriseId: null);
        ArrangeNonGridTeamInfo("T-cs");
        ArrangeNoExistingBinding();

        var sut = CreateSut();
        var outcome = await sut.HandleCallbackAsync("code", "state-cs", ct);

        outcome.ShouldBeOfType<SlackCallbackOutcome.Success>();
        outcome.ClientState.ShouldBe(clientState);
    }

    [Fact]
    public async Task HandleCallbackAsync_PropagatesConsumedClientStateOnGridRefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        const string clientState = """{"targetOrigin":"https://portal.example"}""";
        _stateStore.ConsumeAsync("state-cs-grid", Arg.Any<CancellationToken>())
            .Returns(new SlackOAuthStateEntry(
                State: "state-cs-grid",
                Scopes: DefaultOptions.Scopes,
                RedirectUri: DefaultOptions.RedirectUri,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10),
                ClientState: clientState));
        ArrangeOAuthExchange(
            teamId: "T-grid", botUserId: "U-bot", authedUserId: "U-installer", enterpriseId: "E999");
        ArrangeNonGridTeamInfo("T-grid");

        var sut = CreateSut();
        var outcome = await sut.HandleCallbackAsync("code", "state-cs-grid", ct);

        outcome.ShouldBeOfType<SlackCallbackOutcome.EnterpriseGridUnsupported>();
        outcome.ClientState.ShouldBe(clientState);
    }

    [Fact]
    public async Task HandleCallbackAsync_PropagatesConsumedClientStateOnExchangeFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        const string clientState = """{"targetOrigin":"https://portal.example"}""";
        _stateStore.ConsumeAsync("state-cs-fail", Arg.Any<CancellationToken>())
            .Returns(new SlackOAuthStateEntry(
                State: "state-cs-fail",
                Scopes: DefaultOptions.Scopes,
                RedirectUri: DefaultOptions.RedirectUri,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10),
                ClientState: clientState));
        _http.ExchangeCodeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new SlackOAuthExchangeResult(
                Ok: false,
                Error: "invalid_code",
                TeamId: string.Empty,
                TeamName: null,
                BotUserId: string.Empty,
                BotAccessToken: string.Empty,
                AuthedUserId: string.Empty,
                EnterpriseId: null));

        var sut = CreateSut();
        var outcome = await sut.HandleCallbackAsync("code", "state-cs-fail", ct);

        outcome.ShouldBeOfType<SlackCallbackOutcome.ExchangeFailed>();
        outcome.ClientState.ShouldBe(clientState);
    }

    [Fact]
    public async Task HandleCallbackAsync_InvalidState_ClientStateIsNull()
    {
        // No state entry to consume → no ClientState to propagate. The
        // endpoint falls back to the static "close this tab" HTML.
        var ct = TestContext.Current.CancellationToken;
        _stateStore.ConsumeAsync("nope", Arg.Any<CancellationToken>())
            .Returns((SlackOAuthStateEntry?)null);

        var sut = CreateSut();
        var outcome = await sut.HandleCallbackAsync("code", "nope", ct);

        outcome.ShouldBeOfType<SlackCallbackOutcome.InvalidState>();
        outcome.ClientState.ShouldBeNull();
    }

    [Fact]
    public async Task DisconnectAsync_NotBound_ReturnsNotBound()
    {
        var ct = TestContext.Current.CancellationToken;
        _installStore.GetExistingBindingAsync(Arg.Any<CancellationToken>())
            .Returns((SlackBindingSnapshot?)null);

        var sut = CreateSut();
        var outcome = await sut.DisconnectAsync(ct);

        outcome.ShouldBeOfType<SlackDisconnectOutcome.NotBound>();
        await _installStore.DidNotReceive().DeleteInstallAsync(
            Arg.Any<SlackBindingSnapshot>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisconnectAsync_HappyPath_CallsAuthRevokeThenDeletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var snapshot = new SlackBindingSnapshot(
            TeamId: "T-disc",
            BotTokenSecretName: "slack/T-disc/bot-token",
            SigningSecretSecretName: "slack/T-disc/signing-secret");
        _installStore.GetExistingBindingAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        _installStore.ReadBotTokenAsync(snapshot, Arg.Any<CancellationToken>())
            .Returns("xoxb-stored");
        _http.RevokeTokenAsync("xoxb-stored", Arg.Any<CancellationToken>())
            .Returns(new SlackRevokeResult(Ok: true, Revoked: true, Error: null));

        var sut = CreateSut();
        var outcome = await sut.DisconnectAsync(ct);

        outcome.ShouldBeOfType<SlackDisconnectOutcome.Removed>();

        await _http.Received(1).RevokeTokenAsync("xoxb-stored", Arg.Any<CancellationToken>());
        await _installStore.Received(1).DeleteInstallAsync(snapshot, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisconnectAsync_RevokeFails_StillDeletesLocallyAndReturnsRevokeFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var snapshot = new SlackBindingSnapshot(
            TeamId: "T-disc-fail",
            BotTokenSecretName: "slack/T-disc-fail/bot-token",
            SigningSecretSecretName: "slack/T-disc-fail/signing-secret");
        _installStore.GetExistingBindingAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        _installStore.ReadBotTokenAsync(snapshot, Arg.Any<CancellationToken>())
            .Returns("xoxb-stored");
        _http.RevokeTokenAsync("xoxb-stored", Arg.Any<CancellationToken>())
            .Returns(new SlackRevokeResult(Ok: false, Revoked: false, Error: "invalid_auth"));

        var sut = CreateSut();
        var outcome = await sut.DisconnectAsync(ct);

        var failure = outcome.ShouldBeOfType<SlackDisconnectOutcome.RevokeFailed>();
        failure.Reason.ShouldContain("invalid_auth");

        // Local cleanup must still happen — best-effort remote revoke.
        await _installStore.Received(1).DeleteInstallAsync(snapshot, Arg.Any<CancellationToken>());
    }

    // ---- Arrangement helpers ----

    private void ArrangeValidState(string state)
    {
        _stateStore.ConsumeAsync(state, Arg.Any<CancellationToken>())
            .Returns(new SlackOAuthStateEntry(
                State: state,
                Scopes: DefaultOptions.Scopes,
                RedirectUri: DefaultOptions.RedirectUri,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10),
                ClientState: null));
    }

    private void ArrangeOAuthExchange(
        string teamId,
        string botUserId,
        string authedUserId,
        string? enterpriseId)
    {
        _http.ExchangeCodeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new SlackOAuthExchangeResult(
                Ok: true,
                Error: null,
                TeamId: teamId,
                TeamName: "Test Workspace",
                BotUserId: botUserId,
                BotAccessToken: "xoxb-test",
                AuthedUserId: authedUserId,
                EnterpriseId: enterpriseId));
    }

    private void ArrangeNonGridTeamInfo(string teamId)
    {
        _http.GetTeamInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SlackTeamInfo(teamId, "Test Workspace", EnterpriseId: null));
    }

    private void ArrangeNoExistingBinding()
    {
        _installStore.GetExistingBindingAsync(Arg.Any<CancellationToken>())
            .Returns((SlackBindingSnapshot?)null);
    }

    private SlackOAuthService CreateSut()
    {
        var options = Substitute.For<IOptionsMonitor<SlackOAuthOptions>>();
        options.CurrentValue.Returns(DefaultOptions);
        return new SlackOAuthService(
            _stateStore,
            _http,
            _installStore,
            options,
            NullLoggerFactory.Instance);
    }
}
