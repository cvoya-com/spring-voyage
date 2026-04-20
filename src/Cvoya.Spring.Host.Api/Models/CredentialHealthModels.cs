// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using Cvoya.Spring.Core.CredentialHealth;

/// <summary>
/// Response envelope for the credential-health endpoints
/// (<c>GET /api/v1/agent-runtimes/{id}/credential-health</c> and
/// <c>GET /api/v1/connectors/{slugOrId}/credential-health</c>).
/// </summary>
/// <param name="SubjectId">Runtime id or connector slug.</param>
/// <param name="SecretName">Secret name within the subject (convention: <c>"default"</c> or <c>"api-key"</c>).</param>
/// <param name="Status">Current persistent status.</param>
/// <param name="LastError">Human-readable explanation, or <c>null</c>.</param>
/// <param name="LastChecked">Timestamp of the most recent status update.</param>
public record CredentialHealthResponse(
    string SubjectId,
    string SecretName,
    CredentialHealthStatus Status,
    string? LastError,
    DateTimeOffset LastChecked);

/// <summary>
/// Request body for the <c>POST /…/validate-credential</c> endpoints.
/// The subject's <c>ValidateCredentialAsync</c> is invoked with the
/// supplied credential and the outcome is mirrored into the
/// credential-health store before the result is returned.
/// </summary>
/// <param name="Credential">
/// Raw credential to validate. Connectors that authenticate from
/// multi-part configuration (e.g. GitHub App id + private key) may
/// accept an empty string and validate against stored configuration.
/// </param>
/// <param name="SecretName">
/// Optional secret name for the credential-health row. Defaults to
/// <c>"default"</c>. Callers that store multiple credentials per
/// subject supply a stable name so each row updates independently.
/// </param>
public record CredentialValidateRequest(
    string Credential,
    string? SecretName);

/// <summary>
/// Response body for the <c>POST /…/validate-credential</c> endpoints.
/// </summary>
/// <param name="Valid">
/// <c>true</c> when the credential was accepted, <c>false</c> for every
/// non-valid outcome (including network errors).
/// </param>
/// <param name="Status">
/// Persistent status recorded in the credential-health store after this
/// validation attempt. <c>NetworkError</c> validation outcomes do NOT
/// flip the persistent status — they surface as the previous value (or
/// <c>Unknown</c> for fresh rows).
/// </param>
/// <param name="ErrorMessage">Human-readable error when not valid.</param>
public record CredentialValidateResponse(
    bool Valid,
    CredentialHealthStatus Status,
    string? ErrorMessage);