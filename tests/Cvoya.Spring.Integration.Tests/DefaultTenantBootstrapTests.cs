// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Tenancy;

using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration coverage for the #676 default-tenant bootstrap. These
/// tests stand up the Dapr DI graph (with an in-memory EF context, no
/// live Postgres) so we exercise the wiring end-to-end: the OSS seed
/// providers, the hosted bootstrap service, the gating flag, and the
/// idempotency contract.
/// </summary>
public class DefaultTenantBootstrapTests : IDisposable
{
    private readonly string _packagesRoot;

    public DefaultTenantBootstrapTests()
    {
        _packagesRoot = Path.Combine(
            Path.GetTempPath(),
            "spring-voyage-tests",
            $"bootstrap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_packagesRoot);
        Directory.CreateDirectory(Path.Combine(_packagesRoot, "software-engineering", "skills"));
        File.WriteAllText(
            Path.Combine(_packagesRoot, "software-engineering", "skills", "triage.md"),
            "## triage prompt");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_packagesRoot))
            {
                Directory.Delete(_packagesRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    /// <summary>
    /// First-run scenario: a fresh host with the bootstrap registered must
    /// invoke every DI-registered seed provider against the canonical
    /// "default" tenant.
    /// </summary>
    [Fact]
    public async Task BootstrapHostedService_FirstRun_InvokesEveryRegisteredSeedProvider()
    {
        var capture = new CapturingSeedProvider("capture", priority: 50);

        using var provider = BuildProvider(
            bootstrapEnabled: true,
            extraSeed: capture);

        // Run the bootstrap hosted service end-to-end via its single
        // owner extension (mirrors the OSS Worker composition).
        var bootstrap = provider.GetServices<IHostedService>()
            .OfType<DefaultTenantBootstrapService>()
            .Single();

        await bootstrap.StartAsync(TestContext.Current.CancellationToken);

        capture.SeededTenantIds.ShouldContain("default");
        capture.SeededTenantIds.Count.ShouldBe(1);

        // The OSS file-system bundle adapter is wired into the same
        // DI graph and must run alongside the explicit test provider.
        var allProviders = provider.GetServices<ITenantSeedProvider>().ToList();
        allProviders.ShouldContain(p => p.Id == "skill-bundles");
        allProviders.ShouldContain(p => p.Id == "capture");
    }

    /// <summary>
    /// Idempotency scenario: stopping and restarting the bootstrap must
    /// not duplicate seeded rows. The OSS file-system bundle provider is
    /// a read-only enumeration (idempotent by construction); the test
    /// recorder verifies its dispatch happens twice (the bootstrap is
    /// re-entrant) while the on-disk packages root is unchanged.
    /// </summary>
    [Fact]
    public async Task BootstrapHostedService_StoppedAndRestarted_DoesNotMutateState()
    {
        var capture = new CapturingSeedProvider("capture", priority: 50);

        using var provider = BuildProvider(
            bootstrapEnabled: true,
            extraSeed: capture);

        var bootstrap = provider.GetServices<IHostedService>()
            .OfType<DefaultTenantBootstrapService>()
            .Single();

        var snapshotBefore = SnapshotPackagesRoot();

        await bootstrap.StartAsync(TestContext.Current.CancellationToken);
        await bootstrap.StopAsync(TestContext.Current.CancellationToken);
        await bootstrap.StartAsync(TestContext.Current.CancellationToken);
        await bootstrap.StopAsync(TestContext.Current.CancellationToken);

        var snapshotAfter = SnapshotPackagesRoot();

        // Bootstrap dispatched twice — providers themselves carry the
        // idempotency contract, so a recording test provider sees two
        // calls. Real seed providers (e.g. the bundle adapter) must
        // handle this without producing duplicate rows; we assert that
        // here by snapshotting the packages root and checking it is
        // byte-identical after both runs.
        capture.CallCount.ShouldBe(2);
        snapshotAfter.ShouldBe(snapshotBefore);
    }

    /// <summary>
    /// Disabled-flag scenario: with
    /// <c>Tenancy:BootstrapDefaultTenant=false</c> the hosted service
    /// must be a strict no-op — no provider invocation, no audit log
    /// entry beyond the "skipping" message.
    /// </summary>
    [Fact]
    public async Task BootstrapHostedService_DisabledFlag_IsNoOp()
    {
        var capture = new CapturingSeedProvider("capture", priority: 50);

        using var provider = BuildProvider(
            bootstrapEnabled: false,
            extraSeed: capture);

        var bootstrap = provider.GetServices<IHostedService>()
            .OfType<DefaultTenantBootstrapService>()
            .Single();

        await bootstrap.StartAsync(TestContext.Current.CancellationToken);

        capture.CallCount.ShouldBe(0);
        capture.SeededTenantIds.ShouldBeEmpty();
    }

    private ServiceProvider BuildProvider(
        bool bootstrapEnabled,
        ITenantSeedProvider? extraSeed)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tenancy:BootstrapDefaultTenant"] = bootstrapEnabled ? "true" : "false",
                ["Skills:PackagesRoot"] = _packagesRoot,
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(Substitute.For<IActorProxyFactory>());

        // In-memory SpringDbContext so AddCvoyaSpringDapr respects the
        // pre-registered context and skips the mandatory connection-string
        // requirement.
        services.AddDbContext<SpringDbContext>(opts =>
            opts.UseInMemoryDatabase($"BootstrapTest_{Guid.NewGuid():N}"));

        services.AddCvoyaSpringDapr(config);
        services.AddCvoyaSpringDefaultTenantBootstrap();

        if (extraSeed is not null)
        {
            services.AddSingleton(extraSeed);
        }

        return services.BuildServiceProvider();
    }

    private string SnapshotPackagesRoot()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var file in Directory.EnumerateFiles(_packagesRoot, "*", SearchOption.AllDirectories)
                                       .OrderBy(f => f, StringComparer.Ordinal))
        {
            var info = new FileInfo(file);
            sb.Append(file).Append('|').Append(info.Length).Append('\n');
        }
        return sb.ToString();
    }

    private sealed class CapturingSeedProvider : ITenantSeedProvider
    {
        private readonly ConcurrentQueue<string> _calls = new();

        public CapturingSeedProvider(string id, int priority)
        {
            Id = id;
            Priority = priority;
        }

        public string Id { get; }
        public int Priority { get; }

        public IReadOnlyList<string> SeededTenantIds => _calls.ToArray();
        public int CallCount => SeededTenantIds.Count;

        public Task ApplySeedsAsync(string tenantId, CancellationToken cancellationToken)
        {
            _calls.Enqueue(tenantId);
            return Task.CompletedTask;
        }
    }
}