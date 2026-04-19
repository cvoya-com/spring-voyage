// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.GitHubApp;

using System.IO;
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
            written.ShouldContain("GitHub__AppId=12345");
            written.ShouldContain("GitHub__AppSlug=my-app");
            written.ShouldContain("GitHub__WebhookSecret=whsec_abc");
            // PEM newlines are escaped so the value sits on one line.
            written.ShouldContain("GitHub__PrivateKeyPem=-----BEGIN PRIVATE KEY-----\\nAAAA\\n-----END PRIVATE KEY-----");
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

    private static ManifestConversionResult SampleResult() => new()
    {
        AppId = 12345,
        Slug = "my-app",
        Name = "Spring Voyage (test)",
        Pem = "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----",
        WebhookSecret = "whsec_abc",
        ClientId = "lv1.xxxxxxxxx",
        ClientSecret = "zzzzzzz",
        HtmlUrl = "https://github.com/apps/my-app",
    };
}