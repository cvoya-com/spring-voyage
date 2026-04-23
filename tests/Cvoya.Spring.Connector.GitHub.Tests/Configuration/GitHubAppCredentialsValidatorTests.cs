// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.Configuration;

using System.IO;

using Cvoya.Spring.Connector.GitHub.Auth;

using Shouldly;

using Xunit;

/// <summary>
/// Regression tests for the connector-init PEM validator that backs #609.
/// Every branch maps to one of the scenarios the issue calls out:
/// missing credentials, valid PEM, path-to-valid-PEM, path-to-missing-file,
/// and garbage-that-is-neither.
/// </summary>
public class GitHubAppCredentialsValidatorTests
{
    [Fact]
    public void Classify_BothMissing_ReturnsMissingWithDisabledReason()
    {
        var options = new GitHubConnectorOptions();

        var result = GitHubAppCredentialsValidator.Classify(options);

        result.Classification.ShouldBe(GitHubAppCredentialsValidator.Kind.Missing);
        result.DisabledReason.ShouldNotBeNullOrWhiteSpace();
        result.DisabledReason!.ShouldContain("GitHub App not configured");
        result.ResolvedPrivateKeyPem.ShouldBeNull();
        result.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public void Classify_AppIdOnly_KeyBlank_ReturnsMalformed()
    {
        var options = new GitHubConnectorOptions { AppId = 12345 };

        var result = GitHubAppCredentialsValidator.Classify(options);

        result.Classification.ShouldBe(GitHubAppCredentialsValidator.Kind.Malformed);
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage!.ShouldContain("GitHub:AppId");
    }

    [Fact]
    public void Classify_KeyOnly_AppIdZero_ReturnsMalformed()
    {
        var options = new GitHubConnectorOptions
        {
            AppId = 0,
            PrivateKeyPem = TestPemKey.Value,
        };

        var result = GitHubAppCredentialsValidator.Classify(options);

        result.Classification.ShouldBe(GitHubAppCredentialsValidator.Kind.Malformed);
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage!.ShouldContain("GitHub:AppId");
    }

    [Fact]
    public void Classify_ValidPemContents_ReturnsValid_AdoptingVerbatim()
    {
        var key = TestPemKey.Value;
        var options = new GitHubConnectorOptions
        {
            AppId = 12345,
            PrivateKeyPem = key,
        };

        var result = GitHubAppCredentialsValidator.Classify(options);

        result.Classification.ShouldBe(GitHubAppCredentialsValidator.Kind.Valid);
        result.ResolvedPrivateKeyPem.ShouldBe(key);
        result.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public void Classify_PathToValidPemFile_DereferencesAndReturnsValid()
    {
        // Nice-to-have path-dereference. Keeps Docker secrets / k8s file
        // mounts ergonomic: the operator can point the env var at the
        // mounted file instead of inlining the contents.
        var pemPath = Path.Combine(Path.GetTempPath(), $"spring-gh-{Guid.NewGuid():N}.pem");
        File.WriteAllText(pemPath, TestPemKey.Value);
        try
        {
            var options = new GitHubConnectorOptions
            {
                AppId = 12345,
                PrivateKeyPem = pemPath,
            };

            var result = GitHubAppCredentialsValidator.Classify(options);

            result.Classification.ShouldBe(GitHubAppCredentialsValidator.Kind.Valid);
            result.ResolvedPrivateKeyPem.ShouldNotBeNull();
            result.ResolvedPrivateKeyPem.ShouldContain("-----BEGIN");
            // The dereferenced contents come from the file, not the path.
            result.ResolvedPrivateKeyPem.ShouldNotContain(pemPath);
        }
        finally
        {
            File.Delete(pemPath);
        }
    }

    [Fact]
    public void Classify_PathToMissingFile_ReturnsLooksLikePath()
    {
        // Path that does NOT resolve to a real file. The operator almost
        // certainly meant to mount a secret but didn't — surface the
        // targeted error rather than silently disabling.
        var options = new GitHubConnectorOptions
        {
            AppId = 12345,
            PrivateKeyPem = "/etc/secrets/does-not-exist-" + Guid.NewGuid().ToString("N"),
        };

        var result = GitHubAppCredentialsValidator.Classify(options);

        result.Classification.ShouldBe(GitHubAppCredentialsValidator.Kind.LooksLikePath);
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage!.ShouldContain("filesystem path");
        result.ErrorMessage.ShouldContain("GitHub__PrivateKeyPem");
    }

    [Fact]
    public void Classify_HomeRelativePath_DoesNotResolve_ReturnsLooksLikePath()
    {
        var options = new GitHubConnectorOptions
        {
            AppId = 12345,
            PrivateKeyPem = "~/does-not-exist-" + Guid.NewGuid().ToString("N") + ".pem",
        };

        var result = GitHubAppCredentialsValidator.Classify(options);

        result.Classification.ShouldBe(GitHubAppCredentialsValidator.Kind.LooksLikePath);
    }

    [Fact]
    public void Classify_PathToFileWithGarbage_ReturnsMalformed()
    {
        // File exists but its contents aren't PEM — surface the malformed
        // error with a targeted message. Without this, a typo'd mount (for
        // example an empty file) would silently disable the connector.
        var garbagePath = Path.Combine(Path.GetTempPath(), $"spring-gh-garbage-{Guid.NewGuid():N}.pem");
        File.WriteAllText(garbagePath, "not a pem key at all");
        try
        {
            var options = new GitHubConnectorOptions
            {
                AppId = 12345,
                PrivateKeyPem = garbagePath,
            };

            var result = GitHubAppCredentialsValidator.Classify(options);

            result.Classification.ShouldBe(GitHubAppCredentialsValidator.Kind.Malformed);
            result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
            result.ErrorMessage!.ShouldContain("PEM-encoded");
        }
        finally
        {
            File.Delete(garbagePath);
        }
    }

    [Fact]
    public void Classify_GarbageValue_ReturnsMalformed()
    {
        var options = new GitHubConnectorOptions
        {
            AppId = 12345,
            PrivateKeyPem = "this is not a pem and not a path",
        };

        var result = GitHubAppCredentialsValidator.Classify(options);

        result.Classification.ShouldBe(GitHubAppCredentialsValidator.Kind.Malformed);
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Classify_TruncatedPemBlock_ReturnsMalformed()
    {
        // The operator pasted a broken key (common cause: trailing newline
        // lost during copy). Keep it classified as Malformed so the error
        // message reads "does not parse as PEM" rather than steering toward
        // the path branch.
        var options = new GitHubConnectorOptions
        {
            AppId = 12345,
            PrivateKeyPem = "-----BEGIN PRIVATE KEY-----\nMIIEvwIBADANBgkqhkiG9w0BAQEFAASCBKk\n",
        };

        var result = GitHubAppCredentialsValidator.Classify(options);

        result.Classification.ShouldBe(GitHubAppCredentialsValidator.Kind.Malformed);
    }

    [Fact]
    public void Classify_WindowsStylePathToMissingFile_ReturnsLooksLikePath()
    {
        var options = new GitHubConnectorOptions
        {
            AppId = 12345,
            PrivateKeyPem = @"C:\secrets\github-app-" + Guid.NewGuid().ToString("N") + ".pem",
        };

        var result = GitHubAppCredentialsValidator.Classify(options);

        result.Classification.ShouldBe(GitHubAppCredentialsValidator.Kind.LooksLikePath);
    }

    // -------- #1186: env-file decoding (Firebase/GCP-style `\n` escapes
    // and surrounding-quote tolerance). The connector accepts the same
    // single-line PEM encoding as those ecosystems so podman/docker
    // `--env-file` (which doesn't support multi-line or quote-stripping)
    // is a first-class deployment option. -----------------------------

    [Fact]
    public void Classify_PemWithEscapedNewlines_DecodesAndReturnsValid()
    {
        // Common podman / docker compose --env-file pattern (Firebase /
        // GCP service-account convention): the operator inlines the PEM
        // on a single line, separating blocks with a literal \n that the
        // shell does NOT interpret. The connector must decode this
        // before handing it to RSA.ImportFromPem.
        var encoded = TestPemKey.Value.Replace("\n", "\\n");
        encoded.ShouldNotContain("\n");
        encoded.ShouldContain("\\n");

        var options = new GitHubConnectorOptions
        {
            AppId = 12345,
            PrivateKeyPem = encoded,
        };

        var result = GitHubAppCredentialsValidator.Classify(options);

        result.Classification.ShouldBe(GitHubAppCredentialsValidator.Kind.Valid);
        result.ResolvedPrivateKeyPem.ShouldNotBeNull();
        // The resolved value carries real newlines so downstream code
        // sees a normal multi-line PEM regardless of the input shape.
        result.ResolvedPrivateKeyPem.ShouldContain("\n");
        result.ResolvedPrivateKeyPem.ShouldNotContain("\\n");
    }

    [Fact]
    public void Classify_PemWrappedInDoubleQuotes_StripsQuotesAndReturnsValid()
    {
        // podman --env-file keeps surrounding quotes literally as part
        // of the value. Operators reflexively quote multi-character
        // values, so the validator strips one matching pair defensively
        // (#1186 — the screenshot in the bug report had quote-wrapped
        // values that silently bound as the literal `"..."` string).
        var quoted = "\"" + TestPemKey.Value + "\"";
        var options = new GitHubConnectorOptions
        {
            AppId = 12345,
            PrivateKeyPem = quoted,
        };

        var result = GitHubAppCredentialsValidator.Classify(options);

        result.Classification.ShouldBe(GitHubAppCredentialsValidator.Kind.Valid);
        result.ResolvedPrivateKeyPem.ShouldNotBeNull();
        result.ResolvedPrivateKeyPem.ShouldNotStartWith("\"");
        result.ResolvedPrivateKeyPem.ShouldNotEndWith("\"");
    }

    [Fact]
    public void Classify_PemWrappedInSingleQuotes_StripsQuotesAndReturnsValid()
    {
        var quoted = "'" + TestPemKey.Value + "'";
        var options = new GitHubConnectorOptions
        {
            AppId = 12345,
            PrivateKeyPem = quoted,
        };

        var result = GitHubAppCredentialsValidator.Classify(options);

        result.Classification.ShouldBe(GitHubAppCredentialsValidator.Kind.Valid);
        result.ResolvedPrivateKeyPem.ShouldNotBeNull();
        result.ResolvedPrivateKeyPem.ShouldNotStartWith("'");
    }

    [Fact]
    public void Classify_QuotedAndEscapedPem_ResolvesBoth()
    {
        // Worst-case env-file value: quoted AND escape-encoded. Both
        // transforms must apply for the binder to see a usable PEM.
        var encoded = "\"" + TestPemKey.Value.Replace("\n", "\\n") + "\"";
        var options = new GitHubConnectorOptions
        {
            AppId = 12345,
            PrivateKeyPem = encoded,
        };

        var result = GitHubAppCredentialsValidator.Classify(options);

        result.Classification.ShouldBe(GitHubAppCredentialsValidator.Kind.Valid);
    }

    [Fact]
    public void Classify_EscapeSequenceIgnoredWhenRealNewlinePresent()
    {
        // Defensive: if the value already contains real newlines, do NOT
        // also rewrite literal `\n` sequences — they might appear in a
        // base64 chunk or comment. The happy multi-line case must be
        // adopted verbatim.
        var key = TestPemKey.Value;
        key.ShouldContain("\n");

        var options = new GitHubConnectorOptions
        {
            AppId = 12345,
            PrivateKeyPem = key,
        };

        var result = GitHubAppCredentialsValidator.Classify(options);

        result.Classification.ShouldBe(GitHubAppCredentialsValidator.Kind.Valid);
        result.ResolvedPrivateKeyPem.ShouldBe(key);
    }
}