// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.SlackInstall;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Connector.Slack.Provisioning;

/// <summary>
/// Writes the Slack app credentials returned from
/// <c>apps.manifest.create</c> to one of two persistence targets — the
/// <c>spring.env</c> file (default, zero runtime dependency) or the
/// platform / tenant secret store (<c>spring secret create</c>). Mirrors
/// <see cref="GitHubApp.CredentialWriter"/> but with an all-or-nothing
/// rollback contract on the secret-store paths: issue #2839 mandates that
/// no partial state survive a failure.
///
/// <para>
/// The secret names and the credential bundle are owned by the shared
/// <c>Cvoya.Spring.Connector.Slack.Provisioning</c> kernel
/// (<see cref="SlackSecretNames"/>, <see cref="SlackProvisionedCredentials"/>,
/// <see cref="SlackCredentialSecretMapper"/>) so the CLI and the portal's
/// server-side install endpoint persist byte-identical secrets (#2882).
/// Only the env-file key names below are CLI-specific.
/// </para>
/// </summary>
public static class SlackCredentialWriter
{
    /// <summary>
    /// Env-var keys written out. The runtime binds <c>Slack:OAuth:*</c>
    /// from these at startup (see <c>SlackOAuthOptions</c>).
    /// </summary>
    public static class EnvKeys
    {
        public const string AppId = "Slack__AppId";
        public const string ClientId = "Slack__OAuth__ClientId";
        public const string ClientSecret = "Slack__OAuth__ClientSecret";
        public const string SigningSecret = "Slack__OAuth__SigningSecret";
        public const string VerificationToken = "Slack__OAuth__VerificationToken";
        public const string RedirectUri = "Slack__OAuth__RedirectUri";
    }

    /// <summary>
    /// Result of a credential-write operation. <see cref="MissingFields"/>
    /// lists any fields Slack omitted; the CLI surfaces them as warnings
    /// without aborting.
    /// </summary>
    public sealed record WriteOutcome(
        string Target,
        IReadOnlyList<string> WrittenKeys,
        IReadOnlyList<string> MissingFields);

    /// <summary>
    /// Appends Slack app credentials to <paramref name="envFilePath"/>.
    /// Mirrors the GitHub register flow: existing keys are commented out
    /// with a timestamp before new lines are appended. Env-file writes
    /// are atomic at the filesystem level (single
    /// <c>File.WriteAllTextAsync</c>) so there's no partial-write state
    /// to roll back.
    /// </summary>
    public static async Task<WriteOutcome> WriteEnvAsync(
        SlackProvisionedCredentials credentials,
        string envFilePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(envFilePath))
        {
            throw new ArgumentException("envFilePath is required.", nameof(envFilePath));
        }

        var (pairs, missing) = BuildEnvPairs(credentials);

        var existingLines = File.Exists(envFilePath)
            ? (await File.ReadAllLinesAsync(envFilePath, cancellationToken).ConfigureAwait(false)).ToList()
            : new List<string>();

        var stamp = DateTimeOffset.UtcNow.ToString("O");
        foreach (var (key, _) in pairs)
        {
            for (var i = 0; i < existingLines.Count; i++)
            {
                var line = existingLines[i];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0)
                {
                    continue;
                }
                if (line.AsSpan(0, eq).Trim().Equals(key.AsSpan(), StringComparison.Ordinal))
                {
                    existingLines[i] = $"# {line}  # overwritten by `spring connector slack install` at {stamp}";
                }
            }
        }

        var appended = new StringBuilder();
        appended.AppendLine();
        appended.AppendLine($"# Slack app credentials — written by `spring connector slack install` at {stamp}");
        appended.AppendLine(
            "# Keys bind to the Slack:OAuth:* configuration section at startup. See " +
            "docs/architecture/connectors.md and ADR-0061 for the OSS Slack connector shape.");
        foreach (var (key, value) in pairs)
        {
            appended.AppendLine($"{key}={value}");
        }

        var dir = Path.GetDirectoryName(envFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var final = string.Join(Environment.NewLine, existingLines);
        if (existingLines.Count > 0)
        {
            final += Environment.NewLine;
        }
        final += appended.ToString();
        await File.WriteAllTextAsync(envFilePath, final, cancellationToken).ConfigureAwait(false);

        return new WriteOutcome(
            Target: envFilePath,
            WrittenKeys: pairs.Select(p => p.Key).ToArray(),
            MissingFields: missing);
    }

    /// <summary>
    /// Writes Slack credentials as platform-scoped secrets. The first
    /// failure aborts further writes AND rolls back every secret that
    /// was successfully written in this call — per issue #2839's
    /// "do NOT partially persist" requirement.
    /// </summary>
    public static async Task<WriteOutcome> WriteSecretsAsync(
        SlackProvisionedCredentials credentials,
        SpringApiClient apiClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(apiClient);

        var mapping = SlackCredentialSecretMapper.BuildSecretPairs(credentials);

        var written = new List<string>();
        try
        {
            foreach (var pair in mapping.Pairs)
            {
                await apiClient.CreatePlatformSecretAsync(
                    name: pair.Name,
                    value: pair.Value,
                    externalStoreKey: null,
                    ct: cancellationToken).ConfigureAwait(false);
                written.Add(pair.Name);
            }
        }
        catch
        {
            // Roll back what we've already written so the operator's
            // platform-secret store doesn't end up half-populated. Each
            // rollback is best-effort — we re-throw the original error
            // either way, so the operator sees the actual failure.
            foreach (var name in written)
            {
                try
                {
                    await apiClient.DeletePlatformSecretAsync(name, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // best-effort
                }
            }
            throw;
        }

        return new WriteOutcome(
            Target: "platform secrets (scope=platform)",
            WrittenKeys: written,
            MissingFields: mapping.MissingFields);
    }

    /// <summary>
    /// Writes Slack credentials as tenant-scoped secrets via the Spring
    /// API. The runtime connector resolves OAuth credentials through a
    /// tenant → platform → env precedence chain (issue #2849), so this
    /// path is the recommended persistence target on a multi-user or
    /// multi-tenant deployment. Atomic-rollback contract matches
    /// <see cref="WriteSecretsAsync"/> — any failure rolls back every
    /// secret already written in this call.
    /// </summary>
    public static async Task<WriteOutcome> WriteTenantSecretsAsync(
        SlackProvisionedCredentials credentials,
        SpringApiClient apiClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(apiClient);

        var mapping = SlackCredentialSecretMapper.BuildSecretPairs(credentials);

        var written = new List<string>();
        try
        {
            foreach (var pair in mapping.Pairs)
            {
                await apiClient.CreateTenantSecretAsync(
                    name: pair.Name,
                    value: pair.Value,
                    externalStoreKey: null,
                    ct: cancellationToken).ConfigureAwait(false);
                written.Add(pair.Name);
            }
        }
        catch
        {
            // Roll back what we've already written so the operator's
            // tenant-secret store doesn't end up half-populated. Each
            // rollback is best-effort — we re-throw the original error
            // either way, so the operator sees the actual failure.
            foreach (var name in written)
            {
                try
                {
                    await apiClient.DeleteTenantSecretAsync(name, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // best-effort
                }
            }
            throw;
        }

        return new WriteOutcome(
            Target: "tenant secrets (scope=tenant)",
            WrittenKeys: written,
            MissingFields: mapping.MissingFields);
    }

    private static (IReadOnlyList<(string Key, string Value)> Pairs, IReadOnlyList<string> Missing)
        BuildEnvPairs(SlackProvisionedCredentials c)
    {
        var missing = new List<string>();
        var pairs = new List<(string Key, string Value)>();

        void Add(string key, string? value, string fieldLabel)
        {
            if (!string.IsNullOrEmpty(value))
            {
                pairs.Add((key, value));
            }
            else
            {
                missing.Add(fieldLabel);
            }
        }

        Add(EnvKeys.AppId, c.AppId, "AppId");
        Add(EnvKeys.ClientId, c.ClientId, "ClientId");
        Add(EnvKeys.ClientSecret, c.ClientSecret, "ClientSecret");
        Add(EnvKeys.SigningSecret, c.SigningSecret, "SigningSecret");
        Add(EnvKeys.VerificationToken, c.VerificationToken, "VerificationToken");
        Add(EnvKeys.RedirectUri, c.RedirectUri, "RedirectUri");
        return (pairs, missing);
    }
}
