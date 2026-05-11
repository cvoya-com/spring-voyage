// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

using Cvoya.Spring.Core.Catalog;

/// <summary>
/// Operator-supplied LLM credential at install time (#2159). Carried in
/// <c>PackageInstallTarget.Credentials</c> alongside connector bindings.
/// The pre-flight resolver converts each binding into a tenant-scoped
/// secret write during Phase 2; the canonical secret name is
/// <see cref="CredentialNaming.SecretNameFor"/>(<see cref="Provider"/>,
/// <see cref="AuthMethod"/>).
/// </summary>
/// <param name="Provider">Provider id (e.g. <c>anthropic</c>, <c>openai</c>, <c>google</c>).</param>
/// <param name="AuthMethod">
/// Auth method on the consuming runtime/provider edge — <c>oauth</c> or
/// <c>api-key</c>. Distinguishes Claude Code's OAuth token from Spring
/// Voyage Agent's Anthropic API key on the same provider.
/// </param>
/// <param name="Value">The cleartext secret value the operator typed.</param>
public sealed record CredentialBinding(
    string Provider,
    AuthMethod AuthMethod,
    string Value);

/// <summary>
/// Pre-flight gap surfaced through <see cref="CredentialsMissingException"/>
/// (#2159). Each entry tells the operator exactly which secret to set,
/// the runtime env var the launcher will consume it under, and which
/// scope the gap was detected at.
/// </summary>
/// <param name="Provider">The provider that needs the credential.</param>
/// <param name="AuthMethod">The auth method on the consuming edge.</param>
/// <param name="SecretName">
/// Canonical secret name the resolver looks for, e.g. <c>anthropic-oauth</c>
/// or <c>anthropic-api-key</c>. Computed via
/// <see cref="CredentialNaming.SecretNameFor"/>.
/// </param>
/// <param name="CredentialEnvVar">
/// Env var the runtime launcher will read the resolved value from
/// (e.g. <c>CLAUDE_CODE_OAUTH_TOKEN</c>, <c>ANTHROPIC_API_KEY</c>).
/// Mirrors the runtime catalogue's per-edge entry.
/// </param>
/// <param name="Scope">
/// Where the gap was detected — <c>"package"</c> when the credential is
/// shared across every member unit that consumes this edge, <c>"unit"</c>
/// when only one specific unit needs it. v0.1 always emits
/// <c>"package"</c> because credentials resolve through the
/// (unit, tenant) chain — we either find a tenant default or we don't.
/// </param>
/// <param name="UnitName">Member unit name when <see cref="Scope"/> is <c>"unit"</c>; <c>null</c> otherwise.</param>
/// <param name="ConsumingUnits">
/// Names of every member unit whose <c>(runtime, provider)</c> edge
/// consumes this credential. Surfaced so the wizard / CLI can show the
/// operator <i>which</i> units will be unable to dispatch without the
/// secret. Never empty.
/// </param>
public sealed record CredentialMissing(
    string Provider,
    AuthMethod AuthMethod,
    string SecretName,
    string CredentialEnvVar,
    string Scope,
    string? UnitName,
    IReadOnlyList<string> ConsumingUnits);

/// <summary>
/// Thrown by the install pipeline when one or more required LLM
/// credentials cannot be resolved either from the install request or
/// from the tenant secret store (#2159). The endpoint projects each
/// entry into a structured 400 with <c>code: CredentialsMissing</c> so
/// the wizard / CLI can prompt for the missing values precisely.
/// </summary>
public sealed class CredentialsMissingException : System.Exception
{
    /// <summary>Initialises a new <see cref="CredentialsMissingException"/>.</summary>
    public CredentialsMissingException(IReadOnlyList<CredentialMissing> missing)
        : base(BuildMessage(missing))
    {
        Missing = missing;
    }

    /// <summary>The structured list of missing credentials.</summary>
    public IReadOnlyList<CredentialMissing> Missing { get; }

    private static string BuildMessage(IReadOnlyList<CredentialMissing> missing)
    {
        var parts = new List<string>(missing.Count);
        foreach (var m in missing)
        {
            parts.Add($"{m.SecretName} (env: {m.CredentialEnvVar})");
        }
        return "CredentialsMissing: " + string.Join("; ", parts);
    }
}

/// <summary>
/// Thrown when an install request supplies a credential for a
/// <c>(provider, authMethod)</c> edge that no member unit consumes
/// (#2159). Mirrors <see cref="UnknownConnectorSlugException"/> — a
/// structurally wrong request, surfaced as 400 so the operator
/// notices the typo rather than silently storing an unused secret.
/// </summary>
public sealed class UnknownCredentialEdgeException : System.Exception
{
    /// <summary>Initialises a new <see cref="UnknownCredentialEdgeException"/>.</summary>
    public UnknownCredentialEdgeException(string provider, AuthMethod authMethod)
        : base($"No member unit consumes a '{provider}' / '{FormatAuthMethod(authMethod)}' credential in this package.")
    {
        Provider = provider;
        AuthMethod = authMethod;
    }

    /// <summary>The provider id that was offered without being consumed.</summary>
    public string Provider { get; }

    /// <summary>The auth method that was offered without being consumed.</summary>
    public AuthMethod AuthMethod { get; }

    private static string FormatAuthMethod(AuthMethod method) => method switch
    {
        Cvoya.Spring.Core.Catalog.AuthMethod.Oauth => "oauth",
        Cvoya.Spring.Core.Catalog.AuthMethod.ApiKey => "api-key",
        _ => method.ToString(),
    };
}
