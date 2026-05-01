// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Net;
using System.Net.Http;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Connector.GitHub.Configuration;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.AgentRuntimes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Octokit;

using Shouldly;

using Xunit;

/// <summary>
/// Unit coverage for <see cref="GitHubConnectorType.ValidateCredentialAsync"/>
/// and <see cref="GitHubConnectorType.VerifyContainerBaselineAsync"/> — the two
/// optional <see cref="Connectors.IConnectorType"/> hooks the GitHub connector
/// implements as part of phase 2.11. Drives <see cref="IGitHubInstallationsClient"/>
/// directly so the tests stay independent of Octokit's transport and the GitHub
/// App credential pipeline.
/// </summary>
public class GitHubConnectorTypeCredentialValidationTests
{
    private readonly IUnitConnectorConfigStore _configStore;
    private readonly IUnitConnectorRuntimeStore _runtimeStore;
    private readonly IGitHubWebhookRegistrar _webhookRegistrar;
    private readonly IGitHubInstallationsClient _installationsClient;
    private readonly ILoggerFactory _loggerFactory;

    public GitHubConnectorTypeCredentialValidationTests()
    {
        _configStore = Substitute.For<IUnitConnectorConfigStore>();
        _runtimeStore = Substitute.For<IUnitConnectorRuntimeStore>();
        _webhookRegistrar = Substitute.For<IGitHubWebhookRegistrar>();
        _installationsClient = Substitute.For<IGitHubInstallationsClient>();
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
    }

    [Fact]
    public async Task ValidateCredentialAsync_ConfiguredWithExplicitInstallation_ReturnsValid()
    {
        var sut = CreateSut(
            options: ConfiguredOptions(installationId: 1001));

        _installationsClient.ListInstallationRepositoriesAsync(1001L, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallationRepository(7L, "acme", "repo-a", "acme/repo-a", false),
            });

        var result = await sut.ValidateCredentialAsync(
            credential: string.Empty,
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Status.ShouldBe(CredentialValidationStatus.Valid);
        result.Valid.ShouldBeTrue();
        result.ErrorMessage.ShouldBeNull();

        // No fallback to listing installations when the installation id is set.
        await _installationsClient.DidNotReceiveWithAnyArgs()
            .ListInstallationsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateCredentialAsync_NoConfiguredInstallation_PicksFirstFromList()
    {
        var sut = CreateSut(
            options: ConfiguredOptions(installationId: null));

        _installationsClient.ListInstallationsAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new GitHubInstallation(2002L, "acme", "Organization", "all"),
                new GitHubInstallation(3003L, "alice", "User", "selected"),
            });
        _installationsClient.ListInstallationRepositoriesAsync(2002L, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<GitHubInstallationRepository>());

        var result = await sut.ValidateCredentialAsync(
            credential: string.Empty,
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Status.ShouldBe(CredentialValidationStatus.Valid);
        await _installationsClient.Received(1)
            .ListInstallationRepositoriesAsync(2002L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateCredentialAsync_AppHasNoInstallations_StillValid()
    {
        // The App JWT was accepted (otherwise ListInstallationsAsync would
        // have thrown). With zero installs there's no installation token to
        // exchange, but the App credentials themselves are demonstrably good.
        var sut = CreateSut(
            options: ConfiguredOptions(installationId: null));

        _installationsClient.ListInstallationsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<GitHubInstallation>());

        var result = await sut.ValidateCredentialAsync(
            credential: string.Empty,
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Status.ShouldBe(CredentialValidationStatus.Valid);
        await _installationsClient.DidNotReceiveWithAnyArgs()
            .ListInstallationRepositoriesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateCredentialAsync_ConnectorNotConfigured_ReturnsUnknownWithReason()
    {
        // Empty options → GitHubAppConfigurationRequirement reports Disabled
        // → connector reports Unknown so the credential-health store treats
        // it as "pending / not yet checkable", not "broken".
        var sut = CreateSut(options: new GitHubConnectorOptions());

        var result = await sut.ValidateCredentialAsync(
            credential: string.Empty,
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Status.ShouldBe(CredentialValidationStatus.Unknown);
        result.Valid.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("GitHub App not configured");

        await _installationsClient.DidNotReceiveWithAnyArgs()
            .ListInstallationsAsync(Arg.Any<CancellationToken>());
        await _installationsClient.DidNotReceiveWithAnyArgs()
            .ListInstallationRepositoriesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateCredentialAsync_AuthorizationException_ReturnsInvalid()
    {
        // Octokit raises AuthorizationException for 401/403 from the API —
        // the canonical "credentials rejected" signal.
        var sut = CreateSut(options: ConfiguredOptions(installationId: 1001));

        _installationsClient.ListInstallationRepositoriesAsync(1001L, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AuthorizationException(new ResponseFake(HttpStatusCode.Unauthorized)));

        var result = await sut.ValidateCredentialAsync(
            credential: string.Empty,
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Status.ShouldBe(CredentialValidationStatus.Invalid);
        result.Valid.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
    }

    [Fact]
    public async Task ValidateCredentialAsync_Forbidden_ReturnsInvalid()
    {
        // Some 403 paths (e.g. App suspended) surface as a plain ApiException
        // rather than AuthorizationException. The hook still maps these to
        // Invalid so the credential-health store can flip the unit's badge.
        var sut = CreateSut(options: ConfiguredOptions(installationId: 1001));

        _installationsClient.ListInstallationRepositoriesAsync(1001L, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException("forbidden", HttpStatusCode.Forbidden));

        var result = await sut.ValidateCredentialAsync(
            credential: string.Empty,
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Status.ShouldBe(CredentialValidationStatus.Invalid);
        result.Valid.ShouldBeFalse();
    }

    [Fact]
    public async Task ValidateCredentialAsync_HttpRequestException_ReturnsNetworkError()
    {
        // DNS / TLS / connection failures bubble out of HttpClient as
        // HttpRequestException. Surface as NetworkError so the caller can
        // retry rather than treating the credential as bad.
        var sut = CreateSut(options: ConfiguredOptions(installationId: 1001));

        _installationsClient.ListInstallationRepositoriesAsync(1001L, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("dns lookup failed"));

        var result = await sut.ValidateCredentialAsync(
            credential: string.Empty,
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Status.ShouldBe(CredentialValidationStatus.NetworkError);
        result.Valid.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("dns lookup failed");
    }

    [Fact]
    public async Task ValidateCredentialAsync_ServerError_ReturnsNetworkError()
    {
        // 5xx is "the backing service couldn't tell us". Treat as
        // NetworkError so the credential's validity stays Unknown-ish from
        // the caller's perspective.
        var sut = CreateSut(options: ConfiguredOptions(installationId: 1001));

        _installationsClient.ListInstallationRepositoriesAsync(1001L, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ApiException("github 500", HttpStatusCode.InternalServerError));

        var result = await sut.ValidateCredentialAsync(
            credential: string.Empty,
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Status.ShouldBe(CredentialValidationStatus.NetworkError);
        result.Valid.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyContainerBaselineAsync_ReturnsPassed()
    {
        // The GitHub connector talks to api.github.com over outbound HTTPS
        // — there's no host-side binary to verify. The hook reports Passed
        // (rather than null) so the install / wizard surface renders
        // "checked, OK" instead of "skipped".
        var sut = CreateSut(options: ConfiguredOptions(installationId: 1001));

        var result = await sut.VerifyContainerBaselineAsync(TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Passed.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    private static GitHubConnectorOptions ConfiguredOptions(long? installationId)
    {
        return new GitHubConnectorOptions
        {
            AppId = 12345,
            PrivateKeyPem = TestPemKey.Value,
            WebhookSecret = "test-secret",
            InstallationId = installationId,
        };
    }

    private GitHubConnectorType CreateSut(GitHubConnectorOptions options)
    {
        var optionsAccessor = Options.Create(options);
        var requirement = new GitHubAppConfigurationRequirement(optionsAccessor);

        // Build a minimal service provider so GitHubConnectorType can
        // resolve ISecretStore lazily when list-repositories is called.
        // The tests in this class don't exercise that path, so a no-op
        // stub is sufficient.
        var sp = new ServiceCollection()
            .BuildServiceProvider();

        return new GitHubConnectorType(
            _configStore,
            _runtimeStore,
            _webhookRegistrar,
            _installationsClient,
            Substitute.For<IGitHubCollaboratorsClient>(),
            optionsAccessor,
            requirement,
            Substitute.For<IOAuthSessionStore>(),
            sp,
            Substitute.For<IGitHubUserScopeResolver>(),
            _loggerFactory);
    }

    /// <summary>
    /// Minimal fake of Octokit's <see cref="IResponse"/> so the tests can
    /// raise <see cref="AuthorizationException"/> / <see cref="ApiException"/>
    /// without going through the real HTTP stack.
    /// </summary>
    private sealed class ResponseFake(HttpStatusCode statusCode) : IResponse
    {
        public object Body => string.Empty;

        public IReadOnlyDictionary<string, string> Headers { get; }
            = new Dictionary<string, string>();

        public ApiInfo ApiInfo { get; } = new ApiInfo(
            new Dictionary<string, Uri>(),
            new List<string>(),
            new List<string>(),
            "etag",
            new Octokit.RateLimit(1, 1, 1));

        public HttpStatusCode StatusCode { get; } = statusCode;

        public string ContentType { get; } = "application/json";
    }
}