// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.DependencyInjection;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

/// <summary>
/// Regression tests for the intra-process startup-ordering fix in
/// <see cref="HostMigrationExtensions.MigrateSpringDatabaseAsync"/>
/// (#1608).
/// </summary>
/// <remarks>
/// .NET's Generic Host invokes <see cref="IHostedService.StartAsync"/>
/// in registration order. Several hosted services in
/// <c>AddCvoyaSpringDapr</c>'s graph are registered before
/// <see cref="DatabaseMigrator"/> in the Worker composition; on a fresh
/// PostgreSQL volume one of them queried <c>spring.unit_definitions</c>
/// before the migrator created the table, and the worker logged a
/// <c>42P01: relation "spring.unit_definitions" does not exist</c> per
/// cold start. The cross-process counterpart was fixed in #1607; this
/// covers the intra-process counterpart.
/// </remarks>
public class HostMigrationExtensionsTests
{
    /// <summary>
    /// The ordering invariant: when a host calls
    /// <see cref="HostMigrationExtensions.MigrateSpringDatabaseAsync"/>
    /// before <see cref="IHost.StartAsync(CancellationToken)"/>, the
    /// migrator's <see cref="IHostedService.StartAsync"/> body must
    /// have completed before any other hosted service's
    /// <c>StartAsync</c> is invoked.
    /// </summary>
    [Fact]
    public async Task MigrateSpringDatabaseAsync_RunsMigratorBeforeOtherHostedServices()
    {
        var observer = new HostedServiceOrderObserver();
        using var host = BuildHostWithMigratorAndObserver(observer);

        await host.MigrateSpringDatabaseAsync(TestContext.Current.CancellationToken);

        // After MigrateSpringDatabaseAsync completes, the migrator's
        // first invocation has finished. Other hosted services have
        // not started yet — the host hasn't been told to.
        observer.OtherServicesStarted.ShouldBe(0,
            "no other hosted service may run before the migrator");

        var migrator = host.Services
            .GetServices<IHostedService>()
            .OfType<DatabaseMigrator>()
            .Single();
        migrator.HasRun.ShouldBeTrue(
            "MigrateSpringDatabaseAsync must drive the migrator to completion");

        // Now start the host. The Generic Host invokes every hosted
        // service's StartAsync — the migrator's runs again and is a
        // no-op (HasRun guard); the observer service runs for the
        // first time. Stop immediately.
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            observer.OtherServicesStarted.ShouldBe(1,
                "the observer hosted service must start exactly once after the migrator");
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// A host without <see cref="DatabaseMigrator"/> registered — the
    /// API host in the OSS topology, every test harness that strips
    /// the migrator before <c>BuildServiceProvider</c> — must still
    /// be safe to call <see cref="HostMigrationExtensions.MigrateSpringDatabaseAsync"/>
    /// on. The extension is a no-op when no migrator is registered.
    /// </summary>
    [Fact]
    public async Task MigrateSpringDatabaseAsync_WithNoMigratorRegistered_IsNoOp()
    {
        using var host = Host.CreateDefaultBuilder().Build();

        await Should.NotThrowAsync(
            () => host.MigrateSpringDatabaseAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Calling <see cref="HostMigrationExtensions.MigrateSpringDatabaseAsync"/>
    /// against a <see langword="null"/> host throws
    /// <see cref="ArgumentNullException"/>.
    /// </summary>
    [Fact]
    public async Task MigrateSpringDatabaseAsync_NullHost_Throws()
    {
        await Should.ThrowAsync<ArgumentNullException>(
            () => HostMigrationExtensions.MigrateSpringDatabaseAsync(null!));
    }

    /// <summary>
    /// The Generic Host invokes the migrator's
    /// <see cref="IHostedService.StartAsync"/> as part of normal
    /// hosted-service start-up — a second time, after
    /// <see cref="HostMigrationExtensions.MigrateSpringDatabaseAsync"/>
    /// already ran it. The migrator's idempotency guard
    /// (<see cref="DatabaseMigrator.HasRun"/>) must short-circuit that
    /// second invocation so it does not race against the database
    /// while other hosted services are mid-flight.
    /// </summary>
    [Fact]
    public async Task MigrateSpringDatabaseAsync_SecondInvocationByHost_IsNoOp()
    {
        var observer = new HostedServiceOrderObserver();
        using var host = BuildHostWithMigratorAndObserver(observer);

        await host.MigrateSpringDatabaseAsync(TestContext.Current.CancellationToken);

        var migrator = host.Services
            .GetServices<IHostedService>()
            .OfType<DatabaseMigrator>()
            .Single();
        migrator.HasRun.ShouldBeTrue();

        // host.StartAsync invokes every hosted service's StartAsync.
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // Migrator still HasRun=true — no second-pass migration.
            migrator.HasRun.ShouldBeTrue();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Builds an <see cref="IHost"/> with two hosted services:
    /// <see cref="DatabaseMigrator"/> followed by
    /// <see cref="HostedServiceOrderObserver"/>. The migrator is
    /// configured with the in-memory provider so its body returns at
    /// the <c>IsRelational</c> guard — fast, deterministic, and
    /// exercises the same idempotency-guard path the production
    /// path takes.
    /// </summary>
    private static IHost BuildHostWithMigratorAndObserver(HostedServiceOrderObserver observer)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c =>
            {
                c.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Non-empty so the connection-string guard does
                    // not short-circuit before the IsRelational check.
                    ["ConnectionStrings:SpringDb"] = "Host=test;Database=test;Username=test;Password=test",
                });
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddDbContext<SpringDbContext>(o =>
                    o.UseInMemoryDatabase($"HostMigrationExtensionsTest_{Guid.NewGuid()}"));
                services.AddOptions<DatabaseOptions>().Configure(o => o.AutoMigrate = true);
                services.AddSingleton<ITenantScopeBypass>(_ =>
                    new TenantScopeBypass(NullLogger<TenantScopeBypass>.Instance));

                // The migrator is registered first (mirrors the
                // production registration via
                // AddCvoyaSpringDatabaseMigrator). The observer is
                // registered after — it would normally lose the race
                // on a fresh database, which is exactly the bug
                // MigrateSpringDatabaseAsync prevents by completing
                // the migrator before the host runs StartAsync at all.
                services.AddHostedService<DatabaseMigrator>();
                services.AddSingleton(observer);
                services.AddHostedService(sp => sp.GetRequiredService<HostedServiceOrderObserver>());
            })
            .Build();
    }

    /// <summary>
    /// Hosted service that records every <c>StartAsync</c> invocation
    /// so the test can assert no other hosted service has run yet.
    /// </summary>
    private sealed class HostedServiceOrderObserver : IHostedService
    {
        private int _started;

        public int OtherServicesStarted => Volatile.Read(ref _started);

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _started);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}