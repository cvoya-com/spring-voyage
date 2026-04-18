// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

using System.Security.Cryptography;

/// <summary>
/// Classifies the <see cref="GitHubConnectorOptions.PrivateKeyPem"/> value
/// so <c>AddCvoyaSpringConnectorGitHub</c> can decide whether the connector
/// is fully configured, disabled (missing credentials), or misconfigured in a
/// way that should halt startup (a path passed where PEM contents were
/// expected, or garbage that isn't PEM at all). Pulled out of
/// <see cref="GitHubAppAuth"/> so the failure fires at connector-init time
/// rather than at first hot-path use (#609 — 502 at
/// <c>list-installations</c>).
/// </summary>
/// <remarks>
/// The classifier also implements the path-dereference nice-to-have: when
/// the value IS an existing, readable file whose contents parse as valid
/// PEM, the file contents are adopted transparently. Makes Docker-secrets
/// mounts ergonomic without forcing operators to inline the key into the
/// env var.
/// </remarks>
public static class GitHubAppCredentialsValidator
{
    /// <summary>
    /// The outcome of classifying a value handed to
    /// <see cref="GitHubConnectorOptions.PrivateKeyPem"/>.
    /// </summary>
    public enum Kind
    {
        /// <summary>
        /// <see cref="GitHubConnectorOptions.AppId"/> is zero AND
        /// <see cref="GitHubConnectorOptions.PrivateKeyPem"/> is blank. The
        /// connector treats itself as disabled; it still registers, but
        /// <c>list-installations</c> returns a structured 404 rather than
        /// attempting a JWT sign.
        /// </summary>
        Missing,

        /// <summary>
        /// Both pieces of configuration are present and parse as a usable
        /// private key. <see cref="ValidationResult.ResolvedPrivateKeyPem"/>
        /// carries the PEM contents the rest of the connector should use —
        /// this is either the configured value verbatim, or, in the
        /// path-dereference case, the contents of the file it pointed at.
        /// </summary>
        Valid,

        /// <summary>
        /// The value is clearly not PEM and looks like a filesystem path the
        /// operator intended to mount as a file. Fails startup with a
        /// targeted message pointing at the fix.
        /// </summary>
        LooksLikePath,

        /// <summary>
        /// The value is neither blank, nor a path, nor parseable PEM. Fails
        /// startup so the platform refuses to boot with a broken key rather
        /// than returning a 502 on first use.
        /// </summary>
        Malformed,
    }

    /// <summary>
    /// The classifier result. When <see cref="Classification"/> is
    /// <see cref="Kind.Valid"/>, <see cref="ResolvedPrivateKeyPem"/> holds
    /// the PEM contents that the rest of the connector should consume.
    /// Otherwise <see cref="ResolvedPrivateKeyPem"/> is <c>null</c> and
    /// <see cref="ErrorMessage"/> / <see cref="DisabledReason"/> carry the
    /// user-facing narration.
    /// </summary>
    public sealed record ValidationResult(
        Kind Classification,
        string? ResolvedPrivateKeyPem,
        string? ErrorMessage,
        string? DisabledReason);

    private const string PathInsteadOfPemMessage =
        "Expected PEM contents in 'GitHub:PrivateKeyPem' (env var GITHUB_APP_PRIVATE_KEY); got what looks like a filesystem path. " +
        "If you intended to mount the key as a file, read its contents and set the env var, " +
        "or make sure the path is readable — the connector also accepts a path that points at a file whose contents are valid PEM.";

    private const string MalformedPemMessage =
        "'GitHub:PrivateKeyPem' (env var GITHUB_APP_PRIVATE_KEY) is set but does not parse as a PEM-encoded private key. " +
        "Expected a block delimited by '-----BEGIN ... PRIVATE KEY-----' / '-----END ... PRIVATE KEY-----' " +
        "(or the contents of a .pem file). Paste the full key contents, not a path or a base64 blob.";

    private const string MissingReason =
        "GitHub App not configured. Set 'GitHub:AppId' and 'GitHub:PrivateKeyPem' (env vars GITHUB_APP_ID / GITHUB_APP_PRIVATE_KEY) to enable the GitHub connector.";

    /// <summary>
    /// Classifies the supplied connector options.
    /// </summary>
    /// <param name="options">The bound connector options.</param>
    /// <returns>A <see cref="ValidationResult"/> describing the outcome.</returns>
    public static ValidationResult Classify(GitHubConnectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var rawKey = options.PrivateKeyPem ?? string.Empty;
        var appIdMissing = options.AppId <= 0;
        var keyMissing = string.IsNullOrWhiteSpace(rawKey);

        // Missing = both blank. Partial configuration (e.g. AppId without a
        // key) still counts as misconfigured — we fail fast on the key so
        // the operator sees the same targeted error they'd have gotten at
        // first use.
        if (appIdMissing && keyMissing)
        {
            return new ValidationResult(
                Kind.Missing,
                ResolvedPrivateKeyPem: null,
                ErrorMessage: null,
                DisabledReason: MissingReason);
        }

        // Partial: AppId present but key blank → treat as misconfigured.
        if (!appIdMissing && keyMissing)
        {
            return new ValidationResult(
                Kind.Malformed,
                ResolvedPrivateKeyPem: null,
                ErrorMessage: "'GitHub:AppId' is set but 'GitHub:PrivateKeyPem' (env var GITHUB_APP_PRIVATE_KEY) is empty. Paste the full PEM contents of the GitHub App private key.",
                DisabledReason: null);
        }

        // AppId blank but key present → still misconfigured; the connector
        // cannot mint JWTs without an issuer id.
        if (appIdMissing && !keyMissing)
        {
            return new ValidationResult(
                Kind.Malformed,
                ResolvedPrivateKeyPem: null,
                ErrorMessage: "'GitHub:PrivateKeyPem' is set but 'GitHub:AppId' (env var GITHUB_APP_ID) is zero or missing. Set the numeric GitHub App id.",
                DisabledReason: null);
        }

        // Both supplied — try to interpret the key. Happy path first: does
        // it parse as PEM as-is?
        if (TryImportPem(rawKey))
        {
            return new ValidationResult(
                Kind.Valid,
                ResolvedPrivateKeyPem: rawKey,
                ErrorMessage: null,
                DisabledReason: null);
        }

        // Not PEM on its own. If it LOOKS like a path and points at a
        // readable file whose contents ARE valid PEM, adopt the contents.
        if (LooksLikeFilesystemPath(rawKey))
        {
            var expanded = ExpandUserHome(rawKey.Trim());
            if (File.Exists(expanded))
            {
                string fileContents;
                try
                {
                    fileContents = File.ReadAllText(expanded);
                }
                catch (IOException)
                {
                    // Exists but unreadable — treat as a path-not-PEM error
                    // with the targeted message so the operator knows to
                    // inline the contents or fix the file's permissions.
                    return new ValidationResult(
                        Kind.LooksLikePath,
                        ResolvedPrivateKeyPem: null,
                        ErrorMessage: PathInsteadOfPemMessage,
                        DisabledReason: null);
                }
                catch (UnauthorizedAccessException)
                {
                    return new ValidationResult(
                        Kind.LooksLikePath,
                        ResolvedPrivateKeyPem: null,
                        ErrorMessage: PathInsteadOfPemMessage,
                        DisabledReason: null);
                }

                if (TryImportPem(fileContents))
                {
                    return new ValidationResult(
                        Kind.Valid,
                        ResolvedPrivateKeyPem: fileContents,
                        ErrorMessage: null,
                        DisabledReason: null);
                }

                // File exists but its contents aren't PEM either — same
                // targeted error.
                return new ValidationResult(
                    Kind.Malformed,
                    ResolvedPrivateKeyPem: null,
                    ErrorMessage: MalformedPemMessage,
                    DisabledReason: null);
            }

            return new ValidationResult(
                Kind.LooksLikePath,
                ResolvedPrivateKeyPem: null,
                ErrorMessage: PathInsteadOfPemMessage,
                DisabledReason: null);
        }

        // Not PEM, not a path — classify as malformed.
        return new ValidationResult(
            Kind.Malformed,
            ResolvedPrivateKeyPem: null,
            ErrorMessage: MalformedPemMessage,
            DisabledReason: null);
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="value"/> parses as a PEM-encoded
    /// private key (PKCS#1 or PKCS#8 — anything <see cref="RSA.ImportFromPem"/>
    /// accepts).
    /// </summary>
    private static bool TryImportPem(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Heuristic: does <paramref name="value"/> look like a filesystem path
    /// rather than PEM contents? PEM always contains a '-----BEGIN' header,
    /// so anything without that header that starts with '/' or '~' (or
    /// looks like a Windows drive letter) is almost certainly a path the
    /// operator meant to dereference.
    /// </summary>
    private static bool LooksLikeFilesystemPath(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        // PEM always has the header. If we got here it didn't parse as PEM,
        // but we still want to avoid falsely classifying a broken PEM as a
        // path when it clearly has the header (the user probably pasted
        // something almost-right).
        if (trimmed.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            return false;
        }

        // Unix absolute path or home-relative path.
        if (trimmed.StartsWith('/') || trimmed.StartsWith('~'))
        {
            return true;
        }

        // Windows drive-letter path: C:\foo, D:/bar, etc.
        if (trimmed.Length >= 3
            && char.IsLetter(trimmed[0])
            && trimmed[1] == ':'
            && (trimmed[2] == '\\' || trimmed[2] == '/'))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Expands a leading <c>~</c> to the current user's home directory so
    /// values copied from a shell ergonomically dereference.
    /// </summary>
    private static string ExpandUserHome(string path)
    {
        if (path.Length == 0 || path[0] != '~')
        {
            return path;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return path;
        }

        return path.Length == 1
            ? home
            : Path.Combine(home, path.TrimStart('~').TrimStart('/', '\\'));
    }
}