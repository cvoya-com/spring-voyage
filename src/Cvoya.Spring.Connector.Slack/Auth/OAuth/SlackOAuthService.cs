// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Auth.OAuth;

using Cvoya.Spring.Connector.Slack.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="ISlackOAuthService"/> implementation. Owns the
/// cryptographic state, the <c>oauth.v2.access</c> exchange, the
/// Enterprise Grid probe via <c>team.info</c>, and the disconnect
/// <c>auth.revoke</c> call.
///
/// <para>
/// Real network calls go through <see cref="ISlackOAuthHttpClient"/>;
/// binding persistence + secret writes happen via
/// <see cref="ISlackInstallStore"/>. Both abstractions are
/// substitutable for tests.
/// </para>
/// </summary>
public class SlackOAuthService : ISlackOAuthService
{
    private const string AuthorizeEndpoint = "https://slack.com/oauth/v2/authorize";
    private const int StateBytes = 16;

    private readonly ISlackOAuthStateStore _stateStore;
    private readonly ISlackOAuthHttpClient _http;
    private readonly ISlackInstallStore _installStore;
    private readonly IOptionsMonitor<SlackOAuthOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>Creates a new <see cref="SlackOAuthService"/>.</summary>
    public SlackOAuthService(
        ISlackOAuthStateStore stateStore,
        ISlackOAuthHttpClient http,
        ISlackInstallStore installStore,
        IOptionsMonitor<SlackOAuthOptions> options,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        _stateStore = stateStore;
        _http = http;
        _installStore = installStore;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory.CreateLogger<SlackOAuthService>();
    }

    /// <inheritdoc />
    public async Task<SlackAuthorizeResult> BeginAuthorizationAsync(
        string? clientState,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        EnsureConfigured(options, requireSecret: false);

        var state = TokenGenerator.UrlSafe(StateBytes);
        var entry = new SlackOAuthStateEntry(
            State: state,
            Scopes: options.Scopes,
            RedirectUri: options.RedirectUri,
            ExpiresAt: _timeProvider.GetUtcNow().Add(options.StateTtl),
            ClientState: clientState);
        await _stateStore.SaveAsync(entry, cancellationToken);

        var query = new List<string>
        {
            $"client_id={Uri.EscapeDataString(options.ClientId)}",
            $"redirect_uri={Uri.EscapeDataString(options.RedirectUri)}",
            $"state={Uri.EscapeDataString(state)}",
            $"scope={Uri.EscapeDataString(options.Scopes)}",
        };

        var url = $"{AuthorizeEndpoint}?{string.Join('&', query)}";

        _logger.LogInformation(
            "Issued Slack OAuth authorize URL (state prefix={StatePrefix})",
            state[..Math.Min(6, state.Length)]);

        return new SlackAuthorizeResult(url, state);
    }

    /// <inheritdoc />
    public async Task<SlackCallbackOutcome> HandleCallbackAsync(
        string code,
        string state,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        var options = _options.CurrentValue;
        EnsureConfigured(options, requireSecret: true);

        var entry = await _stateStore.ConsumeAsync(state, cancellationToken);
        if (entry is null)
        {
            return new SlackCallbackOutcome.InvalidState();
        }

        SlackOAuthExchangeResult exchange;
        try
        {
            exchange = await _http.ExchangeCodeAsync(
                options.ClientId, options.ClientSecret, code, entry.RedirectUri, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Slack OAuth code exchange failed");
            return new SlackCallbackOutcome.ExchangeFailed(ex.Message);
        }

        if (!exchange.Ok)
        {
            _logger.LogWarning(
                "Slack oauth.v2.access returned not-ok (error={Error})",
                exchange.Error);
            return new SlackCallbackOutcome.ExchangeFailed(exchange.Error ?? "oauth.v2.access returned ok=false");
        }

        // Enterprise Grid detection — per ADR-0061 §2.3 / §7.6 the v0.1
        // connector refuses Grid installs. The OAuth response itself
        // carries `enterprise.id` when the install lands in a Grid, but
        // we double-check via team.info so the inspection path is the
        // same shape the future Org-mode install will use.
        SlackTeamInfo teamInfo;
        try
        {
            teamInfo = await _http.GetTeamInfoAsync(exchange.BotAccessToken, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Slack team.info probe failed");
            return new SlackCallbackOutcome.ExchangeFailed(ex.Message);
        }

        var enterpriseId = exchange.EnterpriseId ?? teamInfo.EnterpriseId;
        if (!string.IsNullOrEmpty(enterpriseId))
        {
            _logger.LogWarning(
                "Refusing Slack Enterprise Grid install (enterprise_id={EnterpriseId})",
                enterpriseId);
            return new SlackCallbackOutcome.EnterpriseGridUnsupported(
                enterpriseId,
                "Slack Enterprise Grid installs are not supported in v0.1.");
        }

        // ADR-0061 §2.5: one workspace per OSS install. If a binding
        // already exists, the re-install must hit the same team_id; a
        // different team_id requires an explicit Disconnect first.
        var existing = await _installStore.GetExistingBindingAsync(cancellationToken);
        if (existing is not null
            && !string.Equals(existing.TeamId, exchange.TeamId, StringComparison.Ordinal))
        {
            return new SlackCallbackOutcome.WorkspaceConflict(
                ExpectedTeamId: existing.TeamId,
                ReceivedTeamId: exchange.TeamId,
                Reason: $"This tenant is already bound to Slack workspace '{existing.TeamId}'. " +
                        "Disconnect the existing binding before installing a different workspace.");
        }

        await _installStore.PersistInstallAsync(
            new SlackInstallPayload(
                TeamId: exchange.TeamId,
                TeamName: exchange.TeamName,
                BotUserId: exchange.BotUserId,
                BotAccessToken: exchange.BotAccessToken,
                SigningSecret: options.SigningSecret,
                InstallerUserId: exchange.AuthedUserId,
                EnterpriseId: null),
            cancellationToken);

        _logger.LogInformation(
            "Slack binding persisted (team_id={TeamId}, bot_user_id={BotUserId}, installer={InstallerUserId})",
            exchange.TeamId, exchange.BotUserId, exchange.AuthedUserId);

        return new SlackCallbackOutcome.Success(
            TeamId: exchange.TeamId,
            BotUserId: exchange.BotUserId,
            InstallerUserId: exchange.AuthedUserId);
    }

    /// <inheritdoc />
    public async Task<SlackDisconnectOutcome> DisconnectAsync(CancellationToken cancellationToken)
    {
        var binding = await _installStore.GetExistingBindingAsync(cancellationToken);
        if (binding is null)
        {
            return new SlackDisconnectOutcome.NotBound();
        }

        string? botToken = null;
        try
        {
            botToken = await _installStore.ReadBotTokenAsync(binding, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not read the Slack bot token from secret store while disconnecting; " +
                "skipping the remote revoke and deleting the local binding only.");
        }

        SlackDisconnectOutcome? revokeFailure = null;
        if (!string.IsNullOrEmpty(botToken))
        {
            try
            {
                var revoke = await _http.RevokeTokenAsync(botToken, cancellationToken);
                if (!revoke.Ok)
                {
                    _logger.LogWarning(
                        "Slack auth.revoke returned not-ok (error={Error}); continuing with local cleanup.",
                        revoke.Error);
                    revokeFailure = new SlackDisconnectOutcome.RevokeFailed(
                        revoke.Error ?? "auth.revoke returned ok=false");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Slack auth.revoke threw; continuing with local cleanup (best-effort revoke).");
                revokeFailure = new SlackDisconnectOutcome.RevokeFailed(ex.Message);
            }
        }

        await _installStore.DeleteInstallAsync(binding, cancellationToken);

        _logger.LogInformation(
            "Slack binding cleared (team_id={TeamId}).", binding.TeamId);

        return revokeFailure ?? (SlackDisconnectOutcome)new SlackDisconnectOutcome.Removed();
    }

    private static void EnsureConfigured(SlackOAuthOptions options, bool requireSecret)
    {
        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            throw new InvalidOperationException("Slack:OAuth:ClientId is not configured.");
        }
        if (string.IsNullOrWhiteSpace(options.RedirectUri))
        {
            throw new InvalidOperationException("Slack:OAuth:RedirectUri is not configured.");
        }
        if (requireSecret)
        {
            if (string.IsNullOrWhiteSpace(options.ClientSecret))
            {
                throw new InvalidOperationException("Slack:OAuth:ClientSecret is not configured.");
            }
            if (string.IsNullOrWhiteSpace(options.SigningSecret))
            {
                throw new InvalidOperationException("Slack:OAuth:SigningSecret is not configured.");
            }
        }
    }
}
