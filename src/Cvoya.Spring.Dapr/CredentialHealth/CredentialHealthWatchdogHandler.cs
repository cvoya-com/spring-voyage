// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.CredentialHealth;

using System.Net;

using Cvoya.Spring.Core.CredentialHealth;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="DelegatingHandler"/> that inspects outgoing HTTP responses
/// from a named <see cref="HttpClient"/> belonging to an agent runtime
/// or connector, and flips the corresponding
/// <see cref="ICredentialHealthStore"/> row on auth failures.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Status mapping.</strong> The handler treats only auth-level
/// signals as credential-health evidence. Other failures (5xx, timeouts,
/// 429) are left untouched so one upstream outage does not flap the
/// operator-facing status.
/// </para>
/// <list type="bullet">
///   <item><description><c>401 Unauthorized</c> → <see cref="CredentialHealthStatus.Invalid"/></description></item>
///   <item><description><c>403 Forbidden</c> → <see cref="CredentialHealthStatus.Revoked"/></description></item>
///   <item><description>Any other response — no update.</description></item>
/// </list>
/// <para>
/// <strong>Lifecycle.</strong> Attach to a named <c>HttpClient</c> via
/// <see cref="HttpClientBuilderExtensions.AddCredentialHealthWatchdog"/>.
/// The handler instance is resolved per HTTP request by the
/// <c>HttpClientFactory</c>; it is cheap to instantiate. Because
/// <see cref="ICredentialHealthStore"/> is scoped (it holds a
/// <see cref="Data.SpringDbContext"/>), the handler opens a child DI
/// scope per audit write so it can be called from background contexts
/// without an ambient request scope.
/// </para>
/// <para>
/// <strong>Tenant context.</strong> The store resolves
/// <see cref="ITenantContext"/> from the scope it was resolved in. For
/// OSS the tenant is always <c>"default"</c>; cloud overlays that swap
/// in a per-request tenant context must ensure the HttpClient call
/// happens on a request-scoped pipeline so the ambient tenant flows
/// through.
/// </para>
/// </remarks>
public sealed class CredentialHealthWatchdogHandler : DelegatingHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CredentialHealthKind _kind;
    private readonly string _subjectId;
    private readonly string _secretName;
    private readonly ILogger<CredentialHealthWatchdogHandler> _logger;

    /// <summary>
    /// Creates a handler tied to a specific subject. Prefer
    /// <see cref="HttpClientBuilderExtensions.AddCredentialHealthWatchdog"/>
    /// over constructing the handler directly.
    /// </summary>
    /// <param name="scopeFactory">Factory used to open a child DI scope per audit write.</param>
    /// <param name="kind">Whether the subject is a runtime or connector.</param>
    /// <param name="subjectId">Runtime id or connector slug.</param>
    /// <param name="secretName">Secret name within the subject (e.g. <c>"api-key"</c>).</param>
    /// <param name="logger">Diagnostic logger.</param>
    public CredentialHealthWatchdogHandler(
        IServiceScopeFactory scopeFactory,
        CredentialHealthKind kind,
        string subjectId,
        string secretName,
        ILogger<CredentialHealthWatchdogHandler> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        _scopeFactory = scopeFactory;
        _kind = kind;
        _subjectId = subjectId;
        _secretName = secretName;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var status = MapStatus(response.StatusCode);
        if (status is null)
        {
            return response;
        }

        try
        {
            await RecordAsync(status.Value, response.ReasonPhrase, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Diagnostic only — the watchdog must never fail the outgoing
            // request on its own bookkeeping error. A failed write is
            // surfaced in the log so operators notice drift; the caller
            // gets the real response unchanged.
            _logger.LogError(
                ex,
                "Credential-health watchdog: failed to record status for {Kind} '{SubjectId}' / '{SecretName}'.",
                _kind, _subjectId, _secretName);
        }

        return response;
    }

    private async Task RecordAsync(
        CredentialHealthStatus status,
        string? reason,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ICredentialHealthStore>();
        await store.RecordAsync(
            _kind,
            _subjectId,
            _secretName,
            status,
            lastError: reason,
            cancellationToken)
            .ConfigureAwait(false);
    }

    private static CredentialHealthStatus? MapStatus(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized => CredentialHealthStatus.Invalid,
        HttpStatusCode.Forbidden => CredentialHealthStatus.Revoked,
        _ => null,
    };
}