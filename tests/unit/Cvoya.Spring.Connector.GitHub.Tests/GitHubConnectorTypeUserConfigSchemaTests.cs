// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Linq;
using System.Text.Json;

using Cvoya.Spring.Connector.GitHub;
using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Connector.GitHub.Configuration;
using Cvoya.Spring.Connectors;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit coverage for the per-<c>TenantUser</c> display-identity surface the
/// GitHub connector contributes via <see cref="IConnectorType.UserConfigType"/>
/// and <see cref="IConnectorType.GetUserConfigSchemaAsync"/>. Pinned by
/// ADR-0047 §4 / issue #2495 — strictly display identity, no auth fields.
/// </summary>
public class GitHubConnectorTypeUserConfigSchemaTests
{
    [Fact]
    public void UserConfigType_IsGitHubUserConfig()
    {
        var sut = CreateSut();

        sut.UserConfigType.ShouldBe(typeof(GitHubUserConfig));
    }

    [Fact]
    public async Task GetUserConfigSchemaAsync_ReturnsHandAuthoredSchema()
    {
        var sut = CreateSut();

        var schema = await sut.GetUserConfigSchemaAsync(
            TestContext.Current.CancellationToken);

        schema.ShouldNotBeNull();
        var element = schema!.Value;

        element.GetProperty("$schema").GetString()
            .ShouldBe("https://json-schema.org/draft/2020-12/schema");
        element.GetProperty("title").GetString().ShouldBe("GitHubUserConfig");
        element.GetProperty("type").GetString().ShouldBe("object");
    }

    [Fact]
    public async Task GetUserConfigSchemaAsync_ExposesUsernameAndDisplayHandleProperties()
    {
        var sut = CreateSut();

        var schema = await sut.GetUserConfigSchemaAsync(
            TestContext.Current.CancellationToken);

        schema.ShouldNotBeNull();
        var props = schema!.Value.GetProperty("properties");

        props.TryGetProperty("username", out var username).ShouldBeTrue();
        username.GetProperty("type").GetString().ShouldBe("string");

        props.TryGetProperty("display_handle", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task GetUserConfigSchemaAsync_OnlyUsernameIsRequired()
    {
        var sut = CreateSut();

        var schema = await sut.GetUserConfigSchemaAsync(
            TestContext.Current.CancellationToken);

        schema.ShouldNotBeNull();
        var required = schema!.Value.GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();

        required.ShouldBe(new[] { "username" });
    }

    [Fact]
    public async Task GetUserConfigSchemaAsync_DoesNotExposeAuthFields()
    {
        // ADR-0047 §4: the user-config schema is strictly display-identity.
        // PAT, installation override, OAuth-token fields all belong to the
        // unit binding row (ADR-0047 §11) and are described by
        // GetConfigSchemaAsync, not here. Guard against future drift.
        var sut = CreateSut();

        var schema = await sut.GetUserConfigSchemaAsync(
            TestContext.Current.CancellationToken);

        schema.ShouldNotBeNull();
        var props = schema!.Value.GetProperty("properties");

        props.TryGetProperty("pat_secret_name", out _).ShouldBeFalse();
        props.TryGetProperty("app_installation_id", out _).ShouldBeFalse();
        props.TryGetProperty("appInstallationId", out _).ShouldBeFalse();
        props.TryGetProperty("installation_id", out _).ShouldBeFalse();
        props.TryGetProperty("token", out _).ShouldBeFalse();
    }

    [Fact]
    public void GitHubUserConfig_RoundTripsThroughJson()
    {
        var config = new GitHubUserConfig(
            Username: "octocat",
            DisplayHandle: "Octo Cat (@octocat)");
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var json = JsonSerializer.Serialize(config, web);
        var roundTrip = JsonSerializer.Deserialize<GitHubUserConfig>(json, web);

        roundTrip.ShouldNotBeNull();
        roundTrip!.Username.ShouldBe("octocat");
        roundTrip.DisplayHandle.ShouldBe("Octo Cat (@octocat)");

        // The display handle uses snake_case on the wire to match the schema
        // and the rest of the GitHub connector's persisted-config conventions.
        json.ShouldContain("\"display_handle\":");
        json.ShouldNotContain("\"displayHandle\":");
    }

    [Fact]
    public void GitHubUserConfig_DisplayHandleDefaultsToNull()
    {
        var config = new GitHubUserConfig(Username: "octocat");

        config.DisplayHandle.ShouldBeNull();
    }

    private static GitHubConnectorType CreateSut()
    {
        var options = Options.Create(new GitHubConnectorOptions());
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>())
            .Returns(Substitute.For<ILogger>());

        var sp = new ServiceCollection().BuildServiceProvider();

        return new GitHubConnectorType(
            Substitute.For<IUnitConnectorConfigStore>(),
            Substitute.For<IGitHubInstallationsClient>(),
            Substitute.For<IGitHubCollaboratorsClient>(),
            options,
            new GitHubAppConfigurationRequirement(options),
            Substitute.For<IOAuthSessionStore>(),
            sp,
            loggerFactory);
    }
}
