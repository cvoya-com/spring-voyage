// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Configuration;

using System;
using System.IO;
using System.Security.Cryptography;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Dapr.Configuration;
using Cvoya.Spring.Dapr.Secrets;
using Cvoya.Spring.Dapr.Tenancy;
using Cvoya.Spring.Dapr.Tests.Secrets;

using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="SecretsConfigurationRequirement"/>.
/// Saves and restores <c>SPRING_SECRETS_AES_KEY</c> around every test to
/// isolate process-environment state from the test runner's default.
/// </summary>
[Collection(SecretsEnvironmentVariableCollection.Name)]
public class SecretsConfigurationRequirementTests : IDisposable
{
    private const string EnvVar = SecretsKeyClassifier.KeyEnvironmentVariable;
    private readonly string? _savedEnv = Environment.GetEnvironmentVariable(EnvVar);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVar, _savedEnv);
        GC.SuppressFinalize(this);
    }

    private static IOptions<SecretsOptions> Opts(
        bool allowEphemeralDevKey = false,
        string? aesKeyFile = null) =>
        Options.Create(new SecretsOptions
        {
            AllowEphemeralDevKey = allowEphemeralDevKey,
            AesKeyFile = aesKeyFile,
        });

    [Fact]
    public async Task ValidateAsync_ValidEnvKey_ReturnsMet()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Environment.SetEnvironmentVariable(EnvVar, key);

        var requirement = new SecretsConfigurationRequirement(Opts());

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Met);
        status.Severity.ShouldBe(SeverityLevel.Information);
    }

    [Fact]
    public async Task ValidateAsync_ValidFileKey_ReturnsMet()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);
        var path = Path.Combine(Path.GetTempPath(), "spring-secrets-req-" + Guid.NewGuid().ToString("N") + ".key");
        File.WriteAllText(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        try
        {
            var requirement = new SecretsConfigurationRequirement(Opts(aesKeyFile: path));

            var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

            status.Status.ShouldBe(ConfigurationStatus.Met);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ValidateAsync_EphemeralDevKey_ReturnsMetWithWarning()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);
        var requirement = new SecretsConfigurationRequirement(Opts(allowEphemeralDevKey: true));

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Met);
        status.Severity.ShouldBe(SeverityLevel.Warning);
        status.Reason.ShouldNotBeNull();
        status.Reason!.ShouldContain("Ephemeral");
        status.Suggestion.ShouldNotBeNull();
    }

    [Fact]
    public async Task ValidateAsync_NoKeyNoEphemeral_ReturnsInvalidWithFatal()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);
        var requirement = new SecretsConfigurationRequirement(Opts());

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.FatalError.ShouldNotBeNull();
        status.Reason.ShouldNotBeNull();
        status.Suggestion.ShouldNotBeNull();
        status.Suggestion!.ShouldContain(EnvVar);
        status.Suggestion!.ShouldContain("AesKeyFile");
        status.Suggestion!.ShouldContain("AllowEphemeralDevKey");
    }

    [Fact]
    public async Task ValidateAsync_MissingAesKeyFile_ReturnsInvalid()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);
        var path = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".key");
        var requirement = new SecretsConfigurationRequirement(Opts(aesKeyFile: path));

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.FatalError.ShouldNotBeNull();
        status.Reason!.ShouldContain(path);
    }

    [Fact]
    public async Task ValidateAsync_MalformedBase64_ReturnsInvalid()
    {
        Environment.SetEnvironmentVariable(EnvVar, "this is not valid base64!!!");
        var requirement = new SecretsConfigurationRequirement(Opts());

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.FatalError.ShouldNotBeNull();
        status.Reason!.ShouldContain("base64");
    }

    [Fact]
    public async Task ValidateAsync_WrongKeyLength_ReturnsInvalid()
    {
        Environment.SetEnvironmentVariable(EnvVar, Convert.ToBase64String(new byte[16]));
        var requirement = new SecretsConfigurationRequirement(Opts());

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.Reason!.ShouldContain("32 bytes");
    }

    [Fact]
    public async Task ValidateAsync_AllZeroKey_ReturnsInvalid()
    {
        Environment.SetEnvironmentVariable(EnvVar, Convert.ToBase64String(new byte[32]));
        var requirement = new SecretsConfigurationRequirement(Opts());

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.Reason!.ShouldContain("all zeros");
    }

    [Fact]
    public async Task ValidateAsync_SentinelAscendingKey_ReturnsInvalid()
    {
        var bytes = new byte[32];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
        }
        Environment.SetEnvironmentVariable(EnvVar, Convert.ToBase64String(bytes));
        var requirement = new SecretsConfigurationRequirement(Opts());

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.Reason!.ShouldContain("sentinel");
    }

    [Fact]
    public async Task RequirementMetadata_IsStable()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);
        var requirement = new SecretsConfigurationRequirement(Opts(allowEphemeralDevKey: true));

        requirement.RequirementId.ShouldBe("secrets-encryption-key");
        requirement.SubsystemName.ShouldBe("Secrets");
        requirement.IsMandatory.ShouldBeTrue();
        requirement.EnvironmentVariableNames.ShouldContain(EnvVar);
        requirement.EnvironmentVariableNames.ShouldContain("Secrets__AesKeyFile");
        requirement.EnvironmentVariableNames.ShouldContain("Secrets__AllowEphemeralDevKey");
        requirement.ConfigurationSectionPath.ShouldBe(SecretsOptions.SectionName);
        requirement.DocumentationUrl.ShouldNotBeNull();
        await Task.CompletedTask;
    }
}