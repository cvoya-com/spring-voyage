// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.GitHubApp;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Cli.Generated.Models;

/// <summary>
/// Writes the resolved App credentials to one of two persistence
/// targets: <see cref="WriteEnvAsync"/> appends keys to
/// <c>deployment/spring.env</c> (default — zero runtime dependencies,
/// survives <c>deploy.sh</c> restarts), or
/// <see cref="WriteSecretsAsync"/> pipes each value through
/// <c>spring secret --scope platform create</c> (#612) so they land in
/// the platform store that the rest of the stack already reads from.
/// </summary>
public static class CredentialWriter
{
    /// <summary>
    /// Env-var keys written out. Ordering is significant: operators read
    /// the resulting <c>spring.env</c> top-down, and grouping the
    /// App-related keys together keeps the file tidy.
    /// </summary>
    public static class EnvKeys
    {
        public const string AppId = "GitHub__AppId";
        public const string AppSlug = "GitHub__AppSlug";
        public const string PrivateKeyPem = "GitHub__PrivateKeyPem";
        public const string WebhookSecret = "GitHub__WebhookSecret";
        public const string ClientId = "GitHub__OAuth__ClientId";
        public const string ClientSecret = "GitHub__OAuth__ClientSecret";
    }

    /// <summary>
    /// Platform-secret names used with
    /// <c>spring secret --scope platform create</c>. Matches the existing
    /// connector resolver's expectations once tier-1-via-secrets lands
    /// (#615).
    /// </summary>
    public static class SecretNames
    {
        public const string AppId = "github-app-id";
        public const string AppSlug = "github-app-slug";
        public const string PrivateKeyPem = "github-app-private-key-pem";
        public const string WebhookSecret = "github-app-webhook-secret";
        public const string ClientId = "github-oauth-client-id";
        public const string ClientSecret = "github-oauth-client-secret";
    }

    /// <summary>
    /// Result of a credential-write operation. <see cref="MissingFields"/>
    /// lists any fields the GitHub response dropped; the CLI surfaces
    /// them as warnings without aborting.
    /// </summary>
    public sealed record WriteOutcome(
        string Target,
        IReadOnlyList<string> WrittenKeys,
        IReadOnlyList<string> MissingFields);

    /// <summary>
    /// Appends GitHub App credentials to <paramref name="envFilePath"/>.
    /// If the file already defines a given key, the existing line is
    /// commented out (with a timestamp + note) and the new line appended
    /// — preserves the previous value for manual recovery while keeping
    /// the file the source of truth.
    /// </summary>
    public static async Task<WriteOutcome> WriteEnvAsync(
        ManifestConversionResult result,
        string envFilePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(envFilePath))
        {
            throw new ArgumentException("envFilePath is required.", nameof(envFilePath));
        }

        var (pairs, missing) = BuildKeyValuePairs(result);

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
                    existingLines[i] = $"# {line}  # overwritten by `spring github-app register` at {stamp}";
                }
            }
        }

        var appended = new StringBuilder();
        appended.AppendLine();
        appended.AppendLine($"# GitHub App credentials — written by `spring github-app register` at {stamp}");
        appended.AppendLine(
            "# Keys bind to the GitHub:* configuration section at startup. See " +
            "docs/architecture/connectors.md for the fail-open classification.");
        foreach (var (key, value) in pairs)
        {
            appended.AppendLine(FormatEnvLine(key, value));
        }

        // Ensure the directory exists; on a fresh clone the deployment
        // directory is checked in but spring.env itself is gitignored and
        // may not exist yet.
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

    private static string FormatEnvLine(string key, string value)
    {
        // PEM blocks contain embedded newlines. Docker Compose's
        // --env-file syntax does NOT support multi-line values, and
        // neither does Podman (which is what deploy.sh uses). Convert
        // newlines to the literal two-character sequence "\n" — the
        // .NET host reads env vars that way and the GitHub PEM round-
        // trips cleanly.
        //
        // This mirrors the convention documented in spring.env.example
        // for the PrivateKeyPem key.
        var escaped = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return $"{key}={escaped}";
    }

    /// <summary>
    /// Writes credentials as platform-scoped secrets via the existing
    /// <see cref="SpringApiClient"/> wrapper (the same call path used by
    /// <c>spring secret --scope platform create</c>). Each call races
    /// independently; the first failure aborts the remaining writes so
    /// operators don't end up with half the App state in env + half in
    /// secrets.
    /// </summary>
    public static async Task<WriteOutcome> WriteSecretsAsync(
        ManifestConversionResult result,
        SpringApiClient apiClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(apiClient);

        var (pairs, missing) = BuildSecretPairs(result);

        var written = new List<string>();
        foreach (var (name, value) in pairs)
        {
            await apiClient.CreatePlatformSecretAsync(
                name: name,
                value: value,
                externalStoreKey: null,
                ct: cancellationToken).ConfigureAwait(false);
            written.Add(name);
        }

        return new WriteOutcome(
            Target: "platform secrets (scope=platform)",
            WrittenKeys: written,
            MissingFields: missing);
    }

    private static (IReadOnlyList<(string Key, string Value)> Pairs, IReadOnlyList<string> Missing)
        BuildKeyValuePairs(ManifestConversionResult r)
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

        Add(EnvKeys.AppId, r.AppId?.ToString(System.Globalization.CultureInfo.InvariantCulture), "AppId");
        Add(EnvKeys.AppSlug, r.Slug, "Slug");
        Add(EnvKeys.PrivateKeyPem, r.Pem, "Pem");
        Add(EnvKeys.WebhookSecret, r.WebhookSecret, "WebhookSecret");
        Add(EnvKeys.ClientId, r.ClientId, "ClientId");
        Add(EnvKeys.ClientSecret, r.ClientSecret, "ClientSecret");
        return (pairs, missing);
    }

    private static (IReadOnlyList<(string Name, string Value)> Pairs, IReadOnlyList<string> Missing)
        BuildSecretPairs(ManifestConversionResult r)
    {
        var missing = new List<string>();
        var pairs = new List<(string Name, string Value)>();

        void Add(string name, string? value, string fieldLabel)
        {
            if (!string.IsNullOrEmpty(value))
            {
                pairs.Add((name, value));
            }
            else
            {
                missing.Add(fieldLabel);
            }
        }

        Add(SecretNames.AppId, r.AppId?.ToString(System.Globalization.CultureInfo.InvariantCulture), "AppId");
        Add(SecretNames.AppSlug, r.Slug, "Slug");
        Add(SecretNames.PrivateKeyPem, r.Pem, "Pem");
        Add(SecretNames.WebhookSecret, r.WebhookSecret, "WebhookSecret");
        Add(SecretNames.ClientId, r.ClientId, "ClientId");
        Add(SecretNames.ClientSecret, r.ClientSecret, "ClientSecret");
        return (pairs, missing);
    }
}