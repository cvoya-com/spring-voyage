// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.GitHubApp;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Cli.GitHubApp;

using Shouldly;

using Xunit;

public class CredentialWriterTests
{
    [Fact]
    public async Task WriteEnvAsync_AppendsAllFields_WhenFileDoesNotExist()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"spring-test-{System.Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var envPath = Path.Combine(dir, "spring.env");

        try
        {
            var result = SampleResult();

            var outcome = await CredentialWriter.WriteEnvAsync(result, envPath, TestContext.Current.CancellationToken);

            outcome.Target.ShouldBe(envPath);
            outcome.WrittenKeys.ShouldContain(CredentialWriter.EnvKeys.AppId);
            outcome.WrittenKeys.ShouldContain(CredentialWriter.EnvKeys.PrivateKeyPem);
            outcome.WrittenKeys.ShouldContain(CredentialWriter.EnvKeys.WebhookSecret);
            outcome.MissingFields.ShouldBeEmpty();

            var written = await File.ReadAllTextAsync(envPath, TestContext.Current.CancellationToken);
            // Plain-token values stay bare. The numeric AppId in particular must
            // NOT be quoted — quotes would break the .NET long binder (#2960).
            written.ShouldContain("GitHub__AppId=12345");
            written.ShouldNotContain("GitHub__AppId='12345'");
            written.ShouldContain("GitHub__AppSlug=my-app");
            written.ShouldContain("GitHub__WebhookSecret=whsec_abc");
            // PEM newlines are escaped so the value sits on one line, AND the
            // value is single-quoted because it contains whitespace — otherwise
            // a shell that `source`s the file word-splits "RSA PRIVATE KEY" and
            // tries to run "RSA" (#2960).
            written.ShouldContain(
                "GitHub__PrivateKeyPem='-----BEGIN PRIVATE KEY-----\\nAAAA\\n-----END PRIVATE KEY-----'");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteEnvAsync_PreservesExistingUnrelatedKeys()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"spring-test-{System.Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var envPath = Path.Combine(dir, "spring.env");
        await File.WriteAllTextAsync(envPath,
            "POSTGRES_PASSWORD=existing\n" +
            "REDIS_PASSWORD=otherexisting\n",
            TestContext.Current.CancellationToken);

        try
        {
            await CredentialWriter.WriteEnvAsync(SampleResult(), envPath, TestContext.Current.CancellationToken);

            var written = await File.ReadAllTextAsync(envPath, TestContext.Current.CancellationToken);
            written.ShouldContain("POSTGRES_PASSWORD=existing");
            written.ShouldContain("REDIS_PASSWORD=otherexisting");
            written.ShouldContain("GitHub__AppId=12345");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteEnvAsync_CommentsOutExistingGitHubKeys()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"spring-test-{System.Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var envPath = Path.Combine(dir, "spring.env");
        await File.WriteAllTextAsync(envPath,
            "GitHub__AppId=999\n" +
            "GitHub__PrivateKeyPem=oldpem\n",
            TestContext.Current.CancellationToken);

        try
        {
            await CredentialWriter.WriteEnvAsync(SampleResult(), envPath, TestContext.Current.CancellationToken);

            var written = await File.ReadAllTextAsync(envPath, TestContext.Current.CancellationToken);
            // Existing keys stay in the file as comments for audit.
            written.ShouldContain("# GitHub__AppId=999");
            written.ShouldContain("# GitHub__PrivateKeyPem=oldpem");
            // New values appended at the bottom.
            written.ShouldContain("GitHub__AppId=12345");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteEnvAsync_ReportsMissingFields_WhenGitHubDropsValues()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"spring-test-{System.Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var envPath = Path.Combine(dir, "spring.env");

        try
        {
            var result = new ManifestConversionResult
            {
                AppId = 12345,
                Slug = "my-app",
                Pem = "pem-body",
                WebhookSecret = null,     // missing
                ClientId = "lv1.xxx",
                ClientSecret = null,      // missing
            };

            var outcome = await CredentialWriter.WriteEnvAsync(result, envPath, TestContext.Current.CancellationToken);

            outcome.MissingFields.ShouldContain("WebhookSecret");
            outcome.MissingFields.ShouldContain("ClientSecret");
            outcome.WrittenKeys.ShouldContain(CredentialWriter.EnvKeys.AppId);
            outcome.WrittenKeys.ShouldNotContain(CredentialWriter.EnvKeys.WebhookSecret);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A PEM whose header carries the literal "RSA PRIVATE KEY" — the exact
    // shape that made an unquoted env line word-split and run "RSA" (#2960).
    private const string RsaPem =
        "-----BEGIN RSA PRIVATE KEY-----\nMIIEpAIBAAKCAQEA\n-----END RSA PRIVATE KEY-----";

    [Fact]
    public async Task WriteEnvAsync_SingleQuotesPemContainingWhitespace()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"spring-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var envPath = Path.Combine(dir, "spring.env");
        try
        {
            var result = SampleResult(RsaPem);

            await CredentialWriter.WriteEnvAsync(result, envPath, TestContext.Current.CancellationToken);

            var written = await File.ReadAllTextAsync(envPath, TestContext.Current.CancellationToken);
            written.ShouldContain(
                "GitHub__PrivateKeyPem='-----BEGIN RSA PRIVATE KEY-----\\nMIIEpAIBAAKCAQEA\\n-----END RSA PRIVATE KEY-----'");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteEnvAsync_RoundTripsPem_ThroughPosixUnquote()
    {
        // The written PEM line, once a POSIX shell strips the surrounding single
        // quotes (which suppress all expansion), must equal the original value
        // with its literal "\n" intact — that's what the runtime then decodes.
        var dir = Path.Combine(Path.GetTempPath(), $"spring-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var envPath = Path.Combine(dir, "spring.env");
        try
        {
            var result = SampleResult(RsaPem);
            await CredentialWriter.WriteEnvAsync(result, envPath, TestContext.Current.CancellationToken);

            var lines = await File.ReadAllLinesAsync(envPath, TestContext.Current.CancellationToken);
            var pemLine = lines.Single(l => l.StartsWith(
                CredentialWriter.EnvKeys.PrivateKeyPem + "=", StringComparison.Ordinal));

            var rhs = pemLine[(CredentialWriter.EnvKeys.PrivateKeyPem.Length + 1)..];
            var sourced = PosixUnquote(rhs);

            // What `source` yields equals the PEM with real newlines collapsed
            // to literal "\n" — exactly the input the runtime's NormaliseInputKey
            // decodes back to a valid block.
            sourced.ShouldBe(
                "-----BEGIN RSA PRIVATE KEY-----\\nMIIEpAIBAAKCAQEA\\n-----END RSA PRIVATE KEY-----");
            sourced.ShouldContain("\\n");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteEnvAsync_FileSourcesWithoutError_AndPemReadsBack()
    {
        // End-to-end guarantee against #2960: feed the written file to a real
        // POSIX shell via `set -a; source`. Before the fix this exited non-zero
        // with "RSA: command not found". Skips where no POSIX shell exists.
        var shell = FindPosixShell();
        if (shell is null)
        {
            Assert.Skip("No POSIX shell (/bin/bash or /bin/sh) available on this host.");
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), $"spring-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var envPath = Path.Combine(dir, "spring.env");
        try
        {
            var result = SampleResult(RsaPem);
            await CredentialWriter.WriteEnvAsync(result, envPath, TestContext.Current.CancellationToken);

            // Source the file, then print the read-back PEM on a sentinel line so
            // the test can compare it against the original.
            var script =
                "set -a; . \"$1\"; set +a; " +
                "printf 'PEMVALUE=%s\\n' \"$GitHub__PrivateKeyPem\"";

            var (exitCode, stdout, stderr) = RunShell(shell, script, envPath);

            exitCode.ShouldBe(0, $"sourcing spring.env failed: {stderr}");
            stderr.ShouldNotContain("command not found");

            var readBack = stdout
                .Split('\n')
                .Single(l => l.StartsWith("PEMVALUE=", StringComparison.Ordinal))["PEMVALUE=".Length..]
                .TrimEnd('\r');

            // Single quotes suppress backslash interpretation, so the literal
            // "\n" survives verbatim and the value equals the written PEM.
            readBack.ShouldBe(
                "-----BEGIN RSA PRIVATE KEY-----\\nMIIEpAIBAAKCAQEA\\n-----END RSA PRIVATE KEY-----");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Replicates how a POSIX shell strips a single layer of surrounding single
    /// quotes from an env-file RHS, honouring the <c>'\''</c> escape idiom. No
    /// expansion happens inside single quotes, so backslashes are literal.
    /// </summary>
    private static string PosixUnquote(string rhs)
    {
        if (rhs.Length >= 2 && rhs[0] == '\'' && rhs[^1] == '\'')
        {
            var inner = rhs[1..^1];
            return inner.Replace("'\\''", "'", StringComparison.Ordinal);
        }

        return rhs;
    }

    private static string? FindPosixShell()
    {
        foreach (var candidate in new[] { "/bin/bash", "/usr/bin/bash", "/bin/sh", "/usr/bin/sh" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunShell(
        string shell, string script, string argument)
    {
        var psi = new ProcessStartInfo
        {
            FileName = shell,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // `shell -c <script> <arg0> <argument>` → "$1" inside the script is the
        // env-file path; arg0 is a conventional placeholder.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("spring-env-roundtrip");
        psi.ArgumentList.Add(argument);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        return (proc.ExitCode, stdout, stderr);
    }

    private static ManifestConversionResult SampleResult(string? pem = null) => new()
    {
        AppId = 12345,
        Slug = "my-app",
        Name = "Spring Voyage (test)",
        Pem = pem ?? "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----",
        WebhookSecret = "whsec_abc",
        ClientId = "lv1.xxxxxxxxx",
        ClientSecret = "zzzzzzz",
        HtmlUrl = "https://github.com/apps/my-app",
    };
}
