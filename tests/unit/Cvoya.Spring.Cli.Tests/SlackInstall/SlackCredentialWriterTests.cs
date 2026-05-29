// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.SlackInstall;

using System.IO;
using System.Threading.Tasks;

using Cvoya.Spring.Cli.SlackInstall;
using Cvoya.Spring.Connector.Slack.Provisioning;

using Shouldly;

using Xunit;

public class SlackCredentialWriterTests
{
    private static SlackProvisionedCredentials SampleBundle() => new(
        AppId: "A0123456789",
        ClientId: "1234.5678",
        ClientSecret: "client-secret-body",
        SigningSecret: "signing-secret-body",
        VerificationToken: "verification-token-body",
        RedirectUri: "https://sv.example.com/api/v1/tenant/connectors/slack/oauth/callback");

    [Fact]
    public async Task WriteEnvAsync_AppendsAllFields_WhenFileDoesNotExist()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"spring-slack-test-{System.Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var envPath = Path.Combine(dir, "spring.env");
        try
        {
            var outcome = await SlackCredentialWriter.WriteEnvAsync(
                SampleBundle(), envPath, TestContext.Current.CancellationToken);

            outcome.Target.ShouldBe(envPath);
            outcome.WrittenKeys.ShouldContain(SlackCredentialWriter.EnvKeys.AppId);
            outcome.WrittenKeys.ShouldContain(SlackCredentialWriter.EnvKeys.ClientId);
            outcome.WrittenKeys.ShouldContain(SlackCredentialWriter.EnvKeys.ClientSecret);
            outcome.WrittenKeys.ShouldContain(SlackCredentialWriter.EnvKeys.SigningSecret);
            outcome.WrittenKeys.ShouldContain(SlackCredentialWriter.EnvKeys.VerificationToken);
            outcome.WrittenKeys.ShouldContain(SlackCredentialWriter.EnvKeys.RedirectUri);
            outcome.MissingFields.ShouldBeEmpty();

            var written = await File.ReadAllTextAsync(envPath, TestContext.Current.CancellationToken);
            written.ShouldContain("Slack__AppId=A0123456789");
            written.ShouldContain("Slack__OAuth__ClientSecret=client-secret-body");
            written.ShouldContain("Slack__OAuth__SigningSecret=signing-secret-body");
            written.ShouldContain(
                "Slack__OAuth__RedirectUri=https://sv.example.com/api/v1/tenant/connectors/slack/oauth/callback");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteEnvAsync_PreservesExistingUnrelatedKeys()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"spring-slack-test-{System.Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var envPath = Path.Combine(dir, "spring.env");
        await File.WriteAllTextAsync(envPath,
            "POSTGRES_PASSWORD=existing\nREDIS_PASSWORD=otherexisting\n",
            TestContext.Current.CancellationToken);
        try
        {
            await SlackCredentialWriter.WriteEnvAsync(
                SampleBundle(), envPath, TestContext.Current.CancellationToken);

            var written = await File.ReadAllTextAsync(envPath, TestContext.Current.CancellationToken);
            written.ShouldContain("POSTGRES_PASSWORD=existing");
            written.ShouldContain("REDIS_PASSWORD=otherexisting");
            written.ShouldContain("Slack__OAuth__ClientId=1234.5678");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteEnvAsync_CommentsOutExistingSlackKeys()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"spring-slack-test-{System.Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var envPath = Path.Combine(dir, "spring.env");
        await File.WriteAllTextAsync(envPath,
            "Slack__OAuth__ClientSecret=old-secret\nSlack__OAuth__SigningSecret=old-sig\n",
            TestContext.Current.CancellationToken);
        try
        {
            await SlackCredentialWriter.WriteEnvAsync(
                SampleBundle(), envPath, TestContext.Current.CancellationToken);

            var written = await File.ReadAllTextAsync(envPath, TestContext.Current.CancellationToken);
            // Old lines retained as comments for forensic recovery.
            written.ShouldContain("# Slack__OAuth__ClientSecret=old-secret");
            written.ShouldContain("# Slack__OAuth__SigningSecret=old-sig");
            // New lines appended.
            written.ShouldContain("Slack__OAuth__ClientSecret=client-secret-body");
            written.ShouldContain("Slack__OAuth__SigningSecret=signing-secret-body");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WriteEnvAsync_MissingOptionalFields_RecordedInOutcome()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"spring-slack-test-{System.Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        var envPath = Path.Combine(dir, "spring.env");
        try
        {
            // Slack sometimes omits verification_token (and app_id when
            // creation only set a draft). Required fields are still
            // present so the write succeeds with warnings.
            var bundle = new SlackProvisionedCredentials(
                AppId: null,
                ClientId: "1234.5678",
                ClientSecret: "client-secret-body",
                SigningSecret: "signing-secret-body",
                VerificationToken: null,
                RedirectUri: "https://sv.example.com/api/v1/tenant/connectors/slack/oauth/callback");

            var outcome = await SlackCredentialWriter.WriteEnvAsync(
                bundle, envPath, TestContext.Current.CancellationToken);

            outcome.MissingFields.ShouldContain("AppId");
            outcome.MissingFields.ShouldContain("VerificationToken");
            outcome.WrittenKeys.ShouldNotContain(SlackCredentialWriter.EnvKeys.AppId);
            outcome.WrittenKeys.ShouldContain(SlackCredentialWriter.EnvKeys.ClientSecret);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
