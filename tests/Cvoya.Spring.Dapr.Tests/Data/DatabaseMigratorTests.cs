// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Data;

using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;

using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="DatabaseMigrator"/>. The migrator is expected
/// to be a no-op in test scenarios: either because the AutoMigrate
/// flag is off, because no connection string is configured, or
/// because the in-memory EF Core provider is not relational.
/// </summary>
public class DatabaseMigratorTests
{
    [Fact]
    public async Task StartAsync_NonRelationalProvider_DoesNotThrow()
    {
        // InMemory provider + a bogus connection string so we get past
        // the connection-string guard and exercise the IsRelational
        // check against the actual configured provider.
        await using var provider = BuildProvider();
        var migrator = CreateMigrator(
            provider,
            new DatabaseOptions { AutoMigrate = true },
            connectionString: "Host=localhost;Database=notused");

        await Should.NotThrowAsync(() => migrator.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartAsync_AutoMigrateDisabled_DoesNotThrow()
    {
        await using var provider = BuildProvider();
        var migrator = CreateMigrator(
            provider,
            new DatabaseOptions { AutoMigrate = false },
            connectionString: "Host=localhost;Database=notused");

        await Should.NotThrowAsync(() => migrator.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartAsync_NoConnectionString_DoesNotThrow()
    {
        await using var provider = BuildProvider();
        var migrator = CreateMigrator(
            provider,
            new DatabaseOptions { AutoMigrate = true },
            connectionString: null);

        await Should.NotThrowAsync(() => migrator.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StopAsync_CompletesImmediately()
    {
        await using var provider = BuildProvider();
        var migrator = CreateMigrator(
            provider,
            new DatabaseOptions { AutoMigrate = true },
            connectionString: null);

        await migrator.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Regression test for #305. <c>AddCvoyaSpringDapr</c> on its own MUST
    /// NOT register <see cref="DatabaseMigrator"/> as a hosted service —
    /// otherwise both the API and Worker hosts (which both call
    /// <c>AddCvoyaSpringDapr</c>) would race on <c>MigrateAsync</c> against
    /// the same database. The migrator is opt-in via
    /// <see cref="ServiceCollectionExtensions.AddCvoyaSpringDatabaseMigrator"/>
    /// from the single host that owns migrations (the Worker in the OSS
    /// deployment).
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDapr_DoesNotRegisterDatabaseMigrator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IActorProxyFactory>());
        // Pre-register an in-memory SpringDbContext so AddCvoyaSpringDapr
        // skips its mandatory connection-string check.
        services.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase($"DiTest_{Guid.NewGuid():N}"));

        services.AddCvoyaSpringDapr(new ConfigurationBuilder().Build());

        services.ShouldNotContain(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(DatabaseMigrator));
    }

    /// <summary>
    /// The opt-in extension introduced for #305:
    /// <see cref="ServiceCollectionExtensions.AddCvoyaSpringDatabaseMigrator"/>
    /// MUST register <see cref="DatabaseMigrator"/> as a hosted service so
    /// the host that calls it (the Worker in the OSS deployment) actually
    /// applies pending migrations on startup.
    /// </summary>
    [Fact]
    public void AddCvoyaSpringDatabaseMigrator_RegistersDatabaseMigratorHostedService()
    {
        var services = new ServiceCollection();

        services.AddCvoyaSpringDatabaseMigrator();

        services.ShouldContain(d =>
            d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(DatabaseMigrator));
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<SpringDbContext>(o =>
            o.UseInMemoryDatabase($"MigratorTest_{Guid.NewGuid()}"));
        return services.BuildServiceProvider();
    }

    private static DatabaseMigrator CreateMigrator(
        IServiceProvider serviceProvider,
        DatabaseOptions options,
        string? connectionString)
    {
        var configBuilder = new ConfigurationBuilder();
        if (!string.IsNullOrEmpty(connectionString))
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SpringDb"] = connectionString,
            });
        }
        var configuration = configBuilder.Build();

        return new DatabaseMigrator(
            serviceProvider,
            configuration,
            Options.Create(options),
            NullLogger<DatabaseMigrator>.Instance);
    }
}