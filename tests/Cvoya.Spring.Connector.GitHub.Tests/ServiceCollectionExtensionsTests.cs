// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.DependencyInjection;
using Cvoya.Spring.Connector.GitHub.RateLimit;
using Cvoya.Spring.Connector.GitHub.Webhooks;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

public class ServiceCollectionExtensionsTests
{
    private static ServiceProvider BuildProvider(Dictionary<string, string?>? configValues = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues ?? new Dictionary<string, string?>
            {
                ["GitHub:AppId"] = "12345",
                ["GitHub:PrivateKeyPem"] = TestPemKey.Value,
                ["GitHub:WebhookSecret"] = "test-secret",
                ["GitHub:InstallationId"] = "67890"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCvoyaSpringConnectorGitHub(configuration);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersGitHubConnectorOptions()
    {
        using var provider = BuildProvider();

        var options = provider.GetRequiredService<GitHubConnectorOptions>();

        options.ShouldNotBeNull();
        options.AppId.ShouldBe(12345);
        options.WebhookSecret.ShouldBe("test-secret");
        options.InstallationId.ShouldBe(67890);
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersGitHubAppAuth()
    {
        using var provider = BuildProvider();

        var auth = provider.GetRequiredService<GitHubAppAuth>();

        auth.ShouldNotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersGitHubWebhookHandler()
    {
        using var provider = BuildProvider();

        var handler = provider.GetRequiredService<GitHubWebhookHandler>();

        handler.ShouldNotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersGitHubSkillRegistry()
    {
        using var provider = BuildProvider();

        var registry = provider.GetRequiredService<GitHubSkillRegistry>();

        registry.ShouldNotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersGitHubConnector()
    {
        using var provider = BuildProvider();

        var connector = provider.GetRequiredService<GitHubConnector>();

        connector.ShouldNotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RegistersRateLimitTracker()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<IGitHubRateLimitTracker>().ShouldNotBeNull();
        provider.GetRequiredService<GitHubRetryOptions>().ShouldNotBeNull();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_BindsRetryOptions_FromConfiguration()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["GitHub:AppId"] = "12345",
            ["GitHub:PrivateKeyPem"] = TestPemKey.Value,
            ["GitHub:WebhookSecret"] = "test-secret",
            ["GitHub:Retry:MaxRetries"] = "7",
            ["GitHub:Retry:PreflightSafetyThreshold"] = "42"
        });

        var options = provider.GetRequiredService<GitHubRetryOptions>();
        options.MaxRetries.ShouldBe(7);
        options.PreflightSafetyThreshold.ShouldBe(42);
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_UsesTryAdd_DoesNotOverrideExistingRegistrations()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:AppId"] = "12345",
                ["GitHub:PrivateKeyPem"] = TestPemKey.Value,
                ["GitHub:WebhookSecret"] = "test-secret"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCvoyaSpringConnectorGitHub(configuration);

        var connector = services.BuildServiceProvider().GetRequiredService<GitHubConnector>();
        var labelStateMachine = new Cvoya.Spring.Connector.GitHub.Labels.LabelStateMachine(
            Cvoya.Spring.Connector.GitHub.Labels.LabelStateMachineOptions.Default());
        var customRegistry = new GitHubSkillRegistry(connector, labelStateMachine, Substitute.For<IGitHubInstallationsClient>(), Substitute.For<ILoggerFactory>());

        var servicesWithOverride = new ServiceCollection();
        servicesWithOverride.AddLogging();
        servicesWithOverride.AddSingleton(customRegistry);
        servicesWithOverride.AddCvoyaSpringConnectorGitHub(configuration);

        using var provider = servicesWithOverride.BuildServiceProvider();

        var resolved = provider.GetRequiredService<GitHubSkillRegistry>();
        resolved.ShouldBeSameAs(customRegistry);
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_ReturnsSameServiceCollection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();

        var result = services.AddCvoyaSpringConnectorGitHub(configuration);

        result.ShouldBeSameAs(services);
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_DefaultsRateLimitStateStoreToInMemory()
    {
        using var provider = BuildProvider();

        var store = provider.GetRequiredService<IRateLimitStateStore>();
        store.ShouldBeOfType<InMemoryRateLimitStateStore>();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_DaprBackendWithoutDaprClient_ThrowsAtResolve()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["GitHub:AppId"] = "12345",
            ["GitHub:PrivateKeyPem"] = TestPemKey.Value,
            ["GitHub:WebhookSecret"] = "test-secret",
            ["GitHub:InstallationId"] = "67890",
            ["GitHub:RateLimit:StateStore:Backend"] = "dapr",
        });

        // Dapr backend is configured but no DaprClient was registered —
        // DI resolution must fail fast with a clear message rather than
        // silently falling back to in-memory, so operators notice.
        Should.Throw<InvalidOperationException>(() =>
            provider.GetRequiredService<IRateLimitStateStore>());
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_CustomStateStore_Respected()
    {
        var custom = Substitute.For<IRateLimitStateStore>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:AppId"] = "12345",
                ["GitHub:PrivateKeyPem"] = TestPemKey.Value,
                ["GitHub:WebhookSecret"] = "test-secret",
                ["GitHub:InstallationId"] = "67890",
                ["GitHub:RateLimit:StateStore:Backend"] = "dapr",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // Pre-register custom store BEFORE the connector so TryAdd
        // resolves to the caller-supplied instance rather than
        // attempting to materialize the Dapr-backed default.
        services.AddSingleton(custom);
        services.AddCvoyaSpringConnectorGitHub(configuration);

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IRateLimitStateStore>();
        resolved.ShouldBeSameAs(custom);
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_RateLimitTracker_UsesRegisteredStateStore()
    {
        using var provider = BuildProvider();

        var tracker = provider.GetRequiredService<IGitHubRateLimitTracker>();
        tracker.ShouldBeOfType<GitHubRateLimitTracker>();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_ValidCredentials_RegistersEnabledAvailability()
    {
        // Regression for #609. Happy path: valid PEM + AppId → connector
        // reports as enabled so the hot path runs normally.
        using var provider = BuildProvider();

        var availability = provider.GetRequiredService<IGitHubConnectorAvailability>();

        availability.IsEnabled.ShouldBeTrue();
        availability.DisabledReason.ShouldBeNull();
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_MissingCredentials_RegistersDisabledAvailability()
    {
        // Regression for #609. Neither env var set — the connector should
        // register in a "disabled with reason" state rather than throwing,
        // so the rest of the platform boots and list-installations can
        // return a structured 404 instead of a 502.
        using var provider = BuildProvider(new Dictionary<string, string?>());

        var availability = provider.GetRequiredService<IGitHubConnectorAvailability>();

        availability.IsEnabled.ShouldBeFalse();
        availability.DisabledReason.ShouldNotBeNullOrWhiteSpace();
        availability.DisabledReason!.ShouldContain("GitHub App not configured");
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_MalformedPem_ThrowsAtResolve()
    {
        // Regression for #609. Garbage in GITHUB_APP_PRIVATE_KEY — the
        // validator fails fast with a targeted message when the options
        // singleton is resolved, so the host refuses to boot instead of
        // waiting for the first list-installations call to 502.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["GitHub:AppId"] = "12345",
            ["GitHub:PrivateKeyPem"] = "this is not a pem and not a path",
        });

        var ex = Should.Throw<InvalidOperationException>(() =>
            provider.GetRequiredService<GitHubConnectorOptions>());
        ex.Message.ShouldContain("PEM-encoded", Case.Insensitive);
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_PathAsKey_ThrowsWithTargetedMessage()
    {
        // Regression for #609. Path handed where PEM contents were
        // expected. The error message MUST name the env var and explain
        // the fix so operators aren't left staring at "No supported key
        // formats were found" like the original bug report.
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["GitHub:AppId"] = "12345",
            ["GitHub:PrivateKeyPem"] = "/etc/secrets/missing-" + Guid.NewGuid().ToString("N"),
        });

        var ex = Should.Throw<InvalidOperationException>(() =>
            provider.GetRequiredService<GitHubConnectorOptions>());
        ex.Message.ShouldContain("filesystem path", Case.Insensitive);
        ex.Message.ShouldContain("GITHUB_APP_PRIVATE_KEY");
    }

    [Fact]
    public void AddCvoyaSpringConnectorGitHub_PathToValidPemFile_DereferencesAndAdoptsContents()
    {
        // The path-dereference ergonomics test: mount the PEM as a file
        // (Docker secret / k8s volume), point the env var at the path,
        // and the connector should adopt the contents transparently.
        var pemPath = Path.Combine(Path.GetTempPath(), $"spring-gh-{Guid.NewGuid():N}.pem");
        File.WriteAllText(pemPath, TestPemKey.Value);
        try
        {
            using var provider = BuildProvider(new Dictionary<string, string?>
            {
                ["GitHub:AppId"] = "12345",
                ["GitHub:PrivateKeyPem"] = pemPath,
            });

            var options = provider.GetRequiredService<GitHubConnectorOptions>();
            options.PrivateKeyPem.ShouldContain("-----BEGIN");
            options.PrivateKeyPem.ShouldNotBe(pemPath);

            var availability = provider.GetRequiredService<IGitHubConnectorAvailability>();
            availability.IsEnabled.ShouldBeTrue();
        }
        finally
        {
            File.Delete(pemPath);
        }
    }
}