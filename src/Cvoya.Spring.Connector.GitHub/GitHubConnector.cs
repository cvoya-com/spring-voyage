// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub;

using System.Net.Http;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.RateLimit;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

using Octokit;
using Octokit.Internal;

/// <summary>
/// The GitHub connector handles inbound webhook signature validation /
/// translation and authenticates outbound GitHub API calls (used by the
/// runtime-context contributor that mints short-lived installation tokens
/// for agent containers). Per-tool MCP surface for GitHub workloads has
/// been removed (issues #2384 / #2383): agents run <c>gh</c> / <c>git</c>
/// directly inside their container using the credentials delivered via
/// <see cref="GitHubConnectorRuntimeContextContributor"/>.
///
/// <para>
/// Issue #2456 — webhook delivery is App-level only. The platform no
/// longer creates per-repo hooks at unit start; the GitHub App's own
/// installation scope determines what GitHub delivers. The inbound
/// handler resolves the target unit from the payload's
/// <c>(installation_id, owner, repo)</c> via
/// <see cref="Cvoya.Spring.Connectors.IUnitConnectorBindingLookup"/>.
/// </para>
/// </summary>
public class GitHubConnector : IGitHubConnector
{
    /// <summary>
    /// Name of the <see cref="HttpClient"/> / handler chain this connector
    /// resolves through <see cref="IHttpMessageHandlerFactory"/> for Octokit
    /// repo-API calls. Exposed as a constant so the host can attach the
    /// credential-health watchdog (see <c>CONVENTIONS.md</c> § 16) to the
    /// same logical pipeline.
    /// </summary>
    public const string OctokitHttpClientName = "github-octokit";

    private readonly GitHubBindingAuthResolver _authResolver;
    private readonly GitHubWebhookHandler _webhookHandler;
    private readonly IWebhookSignatureValidator _signatureValidator;
    private readonly GitHubConnectorOptions _options;
    private readonly IGitHubRateLimitTracker _rateLimitTracker;
    private readonly GitHubRetryOptions _retryOptions;
    private readonly IHttpMessageHandlerFactory? _handlerFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initialises the connector. ADR-0047 §6: every outbound GitHub call
    /// originating from a unit goes through <see cref="GitHubBindingAuthResolver"/>;
    /// the connector no longer carries an "App JWT" surface of its own.
    /// </summary>
    /// <remarks>
    /// When <paramref name="handlerFactory"/> is supplied, Octokit's inner
    /// HTTP pipeline is sourced from the named handler chain
    /// <see cref="OctokitHttpClientName"/>, which lets the host attach
    /// cross-cutting <see cref="DelegatingHandler"/>s (credential-health
    /// watchdog, proxies) alongside the built-in retry handler without the
    /// connector needing a reference to <c>Cvoya.Spring.Dapr</c>. Left null
    /// (unit tests) the connector falls back to an in-line chain that
    /// wraps the retry handler around a fresh <see cref="HttpClientHandler"/>.
    /// </remarks>
    public GitHubConnector(
        GitHubBindingAuthResolver authResolver,
        GitHubWebhookHandler webhookHandler,
        IWebhookSignatureValidator signatureValidator,
        GitHubConnectorOptions options,
        IGitHubRateLimitTracker rateLimitTracker,
        GitHubRetryOptions retryOptions,
        ILoggerFactory loggerFactory,
        IHttpMessageHandlerFactory? handlerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(authResolver);
        _authResolver = authResolver;
        _webhookHandler = webhookHandler;
        _signatureValidator = signatureValidator;
        _options = options;
        _rateLimitTracker = rateLimitTracker;
        _retryOptions = retryOptions;
        _loggerFactory = loggerFactory;
        _handlerFactory = handlerFactory;
        _logger = loggerFactory.CreateLogger<GitHubConnector>();
    }

    /// <summary>
    /// Gets the webhook handler for processing inbound GitHub events.
    /// </summary>
    public IGitHubWebhookHandler WebhookHandler => _webhookHandler;

    /// <summary>
    /// Processes an incoming webhook payload, validates its signature,
    /// translates the event into one domain message per matching binding
    /// within the receiving tenant per ADR-0047 §10, and applies each
    /// binding's per-binding inbound filter (issue #2407). Bindings whose
    /// filter drops the event are excluded from the result; when every
    /// matching binding drops, the outcome is
    /// <see cref="WebhookOutcome.Ignored"/>, identical to the
    /// "event-type not handled" / "no binding matched" surface.
    /// </summary>
    /// <param name="eventType">The GitHub event type from the X-GitHub-Event header.</param>
    /// <param name="payload">The raw webhook payload body.</param>
    /// <param name="signature">The signature from the X-Hub-Signature-256 header.</param>
    /// <param name="cancellationToken">Cancellation propagated from the request.</param>
    /// <returns>
    /// A <see cref="WebhookHandleResult"/> distinguishing invalid-signature,
    /// accepted-but-ignored, and one-or-more-translated-messages outcomes
    /// so the endpoint can map each to the correct HTTP status (401 /
    /// 202 / 202-with-routing).
    /// </returns>
    public async Task<WebhookHandleResult> HandleWebhookAsync(
        string eventType,
        string payload,
        string signature,
        CancellationToken cancellationToken = default)
    {
        if (!_signatureValidator.Validate(payload, signature, _options.WebhookSecret))
        {
            // `eventType` flows in from the X-GitHub-Event header (attacker-
            // controlled); sanitize before logging to prevent log forging.
            _logger.LogWarning(
                "Invalid webhook signature received for event {EventType}",
                SanitizeForLog(eventType));
            return WebhookHandleResult.InvalidSignature;
        }

        using var document = JsonDocument.Parse(payload);
        var translated = await _webhookHandler
            .TranslateEventAsync(eventType, document.RootElement, cancellationToken)
            .ConfigureAwait(false);

        if (translated.Count == 0)
        {
            // Either the event type was not handled, or no binding in the
            // receiving tenant matched the inbound (owner, repo) pair per
            // ADR-0047 §10. The connector treats both shapes identically —
            // Ignored → webhook endpoint returns 202.
            return WebhookHandleResult.Ignored;
        }

        // Per-binding inbound filter (issue #2407). Each binding has its
        // own filter set; evaluate every translated message independently
        // and keep only the ones whose binding passed. The handler emits
        // an audit ActivityEvent per drop so operators can see why a
        // particular binding suppressed the event.
        var passed = new List<Message>(capacity: translated.Count);
        foreach (var message in translated)
        {
            var filtered = await _webhookHandler
                .ApplyInboundFilterAsync(message, eventType, cancellationToken)
                .ConfigureAwait(false);
            if (filtered is not null)
            {
                passed.Add(filtered);
            }
        }

        return passed.Count == 0
            ? WebhookHandleResult.Ignored
            : WebhookHandleResult.Translated(passed);
    }

    /// <summary>
    /// Strips CR/LF and other control characters from attacker-controlled
    /// values before they reach the log stream, and caps length so a crafted
    /// header or payload field cannot forge fake log entries or flood logs.
    /// Returns "unknown" for null/empty input so log messages remain readable.
    /// </summary>
    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "unknown";
        }

        const int MaxLogValueLength = 128;
        var length = Math.Min(value.Length, MaxLogValueLength);
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            var c = value[i];
            builder.Append(char.IsControl(c) ? '_' : c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Creates an authenticated <see cref="IGitHubClient"/> for the
    /// supplied binding per ADR-0047 §6. The connector resolves the
    /// binding's pinned credential through
    /// <see cref="GitHubBindingAuthResolver"/> — App-installation token
    /// mint when <see cref="UnitGitHubConfig.AppInstallationId"/> is set,
    /// tenant-secret-store PAT read when
    /// <see cref="UnitGitHubConfig.PatSecretName"/> is set — and wires the
    /// result into Octokit. Marked <c>virtual</c> so test subclasses can
    /// substitute a pre-built client without exchanging a JWT for a token
    /// or reading a secret store.
    /// </summary>
    /// <param name="binding">The unit's GitHub binding payload.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An authenticated GitHub client scoped to the binding.</returns>
    public virtual async Task<IGitHubClient> CreateAuthenticatedClientForBindingAsync(
        UnitGitHubConfig binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var credential = await _authResolver
            .ResolveAsync(binding, cancellationToken)
            .ConfigureAwait(false);

        var client = new GitHubClient(BuildConnection(credential.Token));

        _logger.LogDebug(
            "Created authenticated GitHub client for binding (kind={Kind}, repo={Repo}).",
            credential.Kind, SanitizeForLog(binding.Repo));

        return client;
    }

    /// <summary>
    /// Builds the Octokit <see cref="Connection"/> with the rate-limit /
    /// retry <see cref="DelegatingHandler"/> plugged into the underlying
    /// HTTP pipeline. Marked <c>virtual</c> so downstream consumers (e.g. the
    /// cloud repo) can substitute their own pipeline without re-implementing
    /// the whole <see cref="CreateAuthenticatedClientForBindingAsync"/> method.
    /// </summary>
    protected virtual IConnection BuildConnection(string token)
    {
        var httpClient = new HttpClientAdapter(CreateHandler);

        return new Connection(
            new ProductHeaderValue("SpringVoyage"),
            GitHubClient.GitHubApiUrl,
            new InMemoryCredentialStore(new Credentials(token)),
            httpClient,
            new SimpleJsonSerializer());
    }

    private HttpMessageHandler CreateHandler()
    {
        // Prefer the host-registered handler chain so the credential-health
        // watchdog (and any future cross-cutting handler attached by the
        // host via AddHttpClient) sits on top of the connector-owned retry
        // handler. IHttpMessageHandlerFactory returns the configured chain
        // for the named client including primary handler + every registered
        // DelegatingHandler, which is what Octokit's HttpClientAdapter
        // expects. The factory manages the handler lifetime (rotation every
        // few minutes), so we must NOT dispose the returned instance — that
        // matches how IHttpClientFactory-sourced clients behave elsewhere.
        if (_handlerFactory is not null)
        {
            return _handlerFactory.CreateHandler(OctokitHttpClientName);
        }

        // Fallback for direct-construction callers (unit tests). Octokit
        // defaults to HttpClientHandler when its caller provides no inner
        // handler; replicate that and wrap it in the retry handler so every
        // outbound request goes through the rate-limit tracker.
        var retryHandler = new GitHubRetryHandler(
            _rateLimitTracker,
            _retryOptions,
            _loggerFactory)
        {
            InnerHandler = new HttpClientHandler(),
        };

        return retryHandler;
    }
}
