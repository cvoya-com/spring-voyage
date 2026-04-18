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
        result.ErrorMessage.ShouldContain("GITHUB_APP_PRIVATE_KEY");
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
}