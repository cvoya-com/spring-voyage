// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.GitHubApp;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Cli.Commands;
using Cvoya.Spring.Cli.GitHubApp;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <c>spring github-app rotate-key</c> and
/// <c>spring github-app rotate-webhook-secret</c> (#636).
/// Covers dry-run paths, PEM validation, and happy-path persistence
/// to a temp env file.
/// </summary>
[Collection(ConsoleRedirectionCollection.Name)]
public class GitHubAppRotateCommandTests
{
    // -----------------------------------------------------------------------
    // rotate-key
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RotateKey_DryRun_NoFile_PrintsInstructionsWithoutPersisting()
    {
        var stdout = new StringWriter();
        await GitHubAppCommand.RunRotateKeyAsync(
            fromFile: null,
            slug: "my-app",
            writeEnv: false,
            writeSecrets: false,
            envFilePathOverride: null,
            dryRun: true,
            cancellationToken: CancellationToken.None,
            stdout: stdout);

        var output = stdout.ToString();
        output.ShouldContain("rotate-key");
        output.ShouldContain("my-app/keys");
        output.ShouldContain("--dry-run");
    }

    [Fact]
    public async Task RotateKey_DryRun_WithValidPem_PrintsWouldWriteMessage()
    {
        var pemPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(pemPath,
                "-----BEGIN RSA PRIVATE KEY-----\nAAAA\n-----END RSA PRIVATE KEY-----",
                CancellationToken.None);

            var envDir = Path.Combine(Path.GetTempPath(), $"spring-rotate-test-{Guid.NewGuid()}");
            Directory.CreateDirectory(envDir);
            var envPath = Path.Combine(envDir, "spring.env");

            try
            {
                var stdout = new StringWriter();
                await GitHubAppCommand.RunRotateKeyAsync(
                    fromFile: pemPath,
                    slug: null,
                    writeEnv: false,
                    writeSecrets: false,
                    envFilePathOverride: envPath,
                    dryRun: true,
                    cancellationToken: CancellationToken.None,
                    stdout: stdout);

                var output = stdout.ToString();
                output.ShouldContain("PEM validated");
                output.ShouldContain("--dry-run");
                output.ShouldContain(CredentialWriter.EnvKeys.PrivateKeyPem);
                // Nothing should have been persisted.
                File.Exists(envPath).ShouldBeFalse();
            }
            finally
            {
                Directory.Delete(envDir, recursive: true);
            }
        }
        finally
        {
            File.Delete(pemPath);
        }
    }

    [Fact]
    public async Task RotateKey_MissingFile_ThrowsWithExitCode1()
    {
        var ex = await Should.ThrowAsync<GitHubAppRegisterException>(async () =>
        {
            await GitHubAppCommand.RunRotateKeyAsync(
                fromFile: "/does/not/exist.pem",
                slug: null,
                writeEnv: false,
                writeSecrets: false,
                envFilePathOverride: null,
                dryRun: false,
                cancellationToken: CancellationToken.None);
        });

        ex.ExitCode.ShouldBe(1);
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task RotateKey_InvalidPemContent_ThrowsWithExitCode1()
    {
        var pemPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(pemPath, "this is not a pem file", CancellationToken.None);

            var ex = await Should.ThrowAsync<GitHubAppRegisterException>(async () =>
            {
                await GitHubAppCommand.RunRotateKeyAsync(
                    fromFile: pemPath,
                    slug: null,
                    writeEnv: false,
                    writeSecrets: false,
                    envFilePathOverride: null,
                    dryRun: false,
                    cancellationToken: CancellationToken.None);
            });

            ex.ExitCode.ShouldBe(1);
            ex.Message.ShouldContain("valid PEM");
        }
        finally
        {
            File.Delete(pemPath);
        }
    }

    [Fact]
    public async Task RotateKey_NoFromFile_NotDryRun_ThrowsRequiresFile()
    {
        var ex = await Should.ThrowAsync<GitHubAppRegisterException>(async () =>
        {
            await GitHubAppCommand.RunRotateKeyAsync(
                fromFile: null,
                slug: null,
                writeEnv: false,
                writeSecrets: false,
                envFilePathOverride: null,
                dryRun: false,
                cancellationToken: CancellationToken.None);
        });

        ex.ExitCode.ShouldBe(1);
        ex.Message.ShouldContain("--from-file");
    }

    [Fact]
    public async Task RotateKey_WriteEnv_PersistsPemToEnvFile()
    {
        var pemPath = Path.GetTempFileName();
        var envDir = Path.Combine(Path.GetTempPath(), $"spring-rotate-env-{Guid.NewGuid()}");
        Directory.CreateDirectory(envDir);
        var envPath = Path.Combine(envDir, "spring.env");

        try
        {
            var pemContent = "-----BEGIN RSA PRIVATE KEY-----\nAAAA\n-----END RSA PRIVATE KEY-----";
            await File.WriteAllTextAsync(pemPath, pemContent, CancellationToken.None);

            var stdout = new StringWriter();
            await GitHubAppCommand.RunRotateKeyAsync(
                fromFile: pemPath,
                slug: null,
                writeEnv: true,
                writeSecrets: false,
                envFilePathOverride: envPath,
                dryRun: false,
                cancellationToken: CancellationToken.None,
                stdout: stdout);

            File.Exists(envPath).ShouldBeTrue();
            var contents = await File.ReadAllTextAsync(envPath, CancellationToken.None);
            contents.ShouldContain(CredentialWriter.EnvKeys.PrivateKeyPem);
            contents.ShouldContain("-----BEGIN RSA PRIVATE KEY-----");

            var output = stdout.ToString();
            output.ShouldContain("Rotation complete.");
            output.ShouldContain("Restart");
        }
        finally
        {
            File.Delete(pemPath);
            Directory.Delete(envDir, recursive: true);
        }
    }

    [Fact]
    public async Task RotateKey_RejectsBothWriteModes()
    {
        var ex = await Should.ThrowAsync<GitHubAppRegisterException>(async () =>
        {
            await GitHubAppCommand.RunRotateKeyAsync(
                fromFile: null,
                slug: null,
                writeEnv: true,
                writeSecrets: true,
                envFilePathOverride: null,
                dryRun: false,
                cancellationToken: CancellationToken.None);
        });

        ex.Message.ShouldContain("mutually exclusive");
    }

    // -----------------------------------------------------------------------
    // rotate-webhook-secret
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RotateWebhookSecret_DryRun_GeneratesAndPrintsSecret_NoPrompt()
    {
        var stdout = new StringWriter();
        await GitHubAppCommand.RunRotateWebhookSecretAsync(
            fromValue: null,
            writeEnv: false,
            writeSecrets: false,
            envFilePathOverride: null,
            slug: "my-app",
            dryRun: true,
            cancellationToken: CancellationToken.None,
            stdout: stdout);

        var output = stdout.ToString();
        output.ShouldContain("rotate-webhook-secret");
        output.ShouldContain("--dry-run");
        // The generated secret should appear in the output.
        output.ShouldContain("New webhook secret");
    }

    [Fact]
    public async Task RotateWebhookSecret_FromValueTooShort_ThrowsWithExitCode1()
    {
        var ex = await Should.ThrowAsync<GitHubAppRegisterException>(async () =>
        {
            await GitHubAppCommand.RunRotateWebhookSecretAsync(
                fromValue: "short",
                writeEnv: false,
                writeSecrets: false,
                envFilePathOverride: null,
                slug: null,
                dryRun: false,
                cancellationToken: CancellationToken.None);
        });

        ex.ExitCode.ShouldBe(1);
        ex.Message.ShouldContain("16 characters");
    }

    [Fact]
    public async Task RotateWebhookSecret_RejectsBothWriteModes()
    {
        var ex = await Should.ThrowAsync<GitHubAppRegisterException>(async () =>
        {
            await GitHubAppCommand.RunRotateWebhookSecretAsync(
                fromValue: null,
                writeEnv: true,
                writeSecrets: true,
                envFilePathOverride: null,
                slug: null,
                dryRun: false,
                cancellationToken: CancellationToken.None);
        });

        ex.Message.ShouldContain("mutually exclusive");
    }

    [Fact]
    public async Task RotateWebhookSecret_WriteEnv_UserConfirms_PersistsSecretToEnvFile()
    {
        var envDir = Path.Combine(Path.GetTempPath(), $"spring-rotate-whs-{Guid.NewGuid()}");
        Directory.CreateDirectory(envDir);
        var envPath = Path.Combine(envDir, "spring.env");

        try
        {
            var suppliedSecret = "a-valid-secret-that-is-long-enough-for-testing";
            var stdout = new StringWriter();

            // Inject a confirmation that always returns true (simulates
            // the operator pressing 'y' after pasting the secret on GitHub).
            await GitHubAppCommand.RunRotateWebhookSecretAsync(
                fromValue: suppliedSecret,
                writeEnv: true,
                writeSecrets: false,
                envFilePathOverride: envPath,
                slug: null,
                dryRun: false,
                cancellationToken: CancellationToken.None,
                stdout: stdout,
                confirmationPrompt: _ => Task.FromResult(true));

            File.Exists(envPath).ShouldBeTrue();
            var contents = await File.ReadAllTextAsync(envPath, CancellationToken.None);
            contents.ShouldContain(CredentialWriter.EnvKeys.WebhookSecret);
            contents.ShouldContain(suppliedSecret);

            var output = stdout.ToString();
            output.ShouldContain("Rotation complete.");
        }
        finally
        {
            Directory.Delete(envDir, recursive: true);
        }
    }

    [Fact]
    public async Task RotateWebhookSecret_UserDeclines_ConfirmationIsReached_FileNotWrittenBeforeConfirm()
    {
        // When the user declines (types 'n'), the file must not be written.
        // The implementation calls Environment.Exit(1) after decline — that
        // terminates the process in production. In tests, Environment.Exit
        // surfaces as a process exit that the test runner catches as an
        // abnormal session end; to avoid that, we verify the boundary
        // condition: the env file is not created before the confirmation
        // prompt fires (the write happens after). We do this by checking
        // that the confirm callback is called before any write, and that
        // the file doesn't exist at that point.
        var envDir = Path.Combine(Path.GetTempPath(), $"spring-rotate-whs-abort-{Guid.NewGuid()}");
        Directory.CreateDirectory(envDir);
        var envPath = Path.Combine(envDir, "spring.env");

        try
        {
            var fileExistedAtConfirmTime = true; // set to false inside the prompt

            // The confirm seam is injected; when it fires, check that the file
            // has not yet been written (it should not exist), then throw to
            // abort the rest of the flow without calling Environment.Exit.
            await Should.ThrowAsync<OperationCanceledException>(async () =>
            {
                await GitHubAppCommand.RunRotateWebhookSecretAsync(
                    fromValue: "a-valid-secret-that-is-long-enough-for-testing",
                    writeEnv: true,
                    writeSecrets: false,
                    envFilePathOverride: envPath,
                    slug: null,
                    dryRun: false,
                    cancellationToken: CancellationToken.None,
                    stdout: new StringWriter(),
                    confirmationPrompt: _ =>
                    {
                        // Record whether the file existed at confirm time.
                        fileExistedAtConfirmTime = File.Exists(envPath);
                        // Throw to abort execution without calling Environment.Exit.
                        throw new OperationCanceledException("test-abort");
                    });
            });

            // The env write happens AFTER the confirmation, so the file must
            // not have existed when the confirm callback ran.
            fileExistedAtConfirmTime.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(envDir, recursive: true);
        }
    }
}