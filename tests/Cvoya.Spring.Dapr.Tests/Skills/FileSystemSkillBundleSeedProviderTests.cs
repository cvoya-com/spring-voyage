// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="FileSystemSkillBundleSeedProvider"/>. Verify the
/// adapter enumerates the packages root, binds every discovered bundle
/// with <c>enabled=true</c> via
/// <see cref="ITenantSkillBundleBindingService"/>, and degrades cleanly
/// (warn + return) when the packages root is missing.
/// </summary>
public class FileSystemSkillBundleSeedProviderTests : IDisposable
{
    private readonly string _root;

    public FileSystemSkillBundleSeedProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "spring-voyage-tests", $"seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void Id_Is_Stable_And_Priority_Is_DocumentedSlot()
    {
        var (sut, _) = CreateSut();

        sut.Id.ShouldBe("skill-bundles");
        sut.Priority.ShouldBe(10);
    }

    [Fact]
    public async Task ApplySeedsAsync_EnumeratesPackages_And_LogsEachOne()
    {
        WritePackage("software-engineering", new[] { "triage.md", "implement.md" });
        WritePackage("research", new[] { "summarise.md" });

        var logger = new CapturingLogger<FileSystemSkillBundleSeedProvider>();
        var (sut, _) = CreateSut(logger);

        await sut.ApplySeedsAsync("default", TestContext.Current.CancellationToken);

        var infos = logger.Entries.Where(e => e.Level == LogLevel.Information).ToArray();
        infos.Count(e => e.Message.Contains("software-engineering")).ShouldBe(1);
        infos.Count(e => e.Message.Contains("research")).ShouldBe(1);
        infos.Count(e => e.Message.Contains("bound 2 package")).ShouldBe(1);
    }

    [Fact]
    public async Task ApplySeedsAsync_BindsEveryDiscoveredPackage()
    {
        WritePackage("software-engineering", new[] { "triage.md" });
        WritePackage("research", new[] { "summarise.md" });

        var (sut, bindingService) = CreateSut();

        await sut.ApplySeedsAsync("default", TestContext.Current.CancellationToken);

        await bindingService.Received(1).BindAsync(
            "software-engineering",
            enabled: true,
            Arg.Any<CancellationToken>());
        await bindingService.Received(1).BindAsync(
            "research",
            enabled: true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplySeedsAsync_NoPackagesRoot_LogsWarningAndReturns()
    {
        var logger = new CapturingLogger<FileSystemSkillBundleSeedProvider>();
        var (sut, bindingService) = CreateSut(logger, rootConfigured: false);

        await Should.NotThrowAsync(
            () => sut.ApplySeedsAsync("default", TestContext.Current.CancellationToken));

        logger.Entries.ShouldContain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("not configured"));
        await bindingService.DidNotReceive().BindAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplySeedsAsync_PackagesRootMissing_LogsWarningAndReturns()
    {
        var logger = new CapturingLogger<FileSystemSkillBundleSeedProvider>();
        var (sut, bindingService) = CreateSut(
            logger,
            customRoot: Path.Combine(_root, "does-not-exist"));

        await Should.NotThrowAsync(
            () => sut.ApplySeedsAsync("default", TestContext.Current.CancellationToken));

        logger.Entries.ShouldContain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("does not exist"));
        await bindingService.DidNotReceive().BindAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplySeedsAsync_TenantIdRequired()
    {
        var (sut, _) = CreateSut();

        await Should.ThrowAsync<ArgumentException>(
            () => sut.ApplySeedsAsync(string.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplySeedsAsync_RunTwice_IsIdempotent()
    {
        // The service-level BindAsync is upsert-shaped. The provider must
        // call it exactly once per package per pass, and a second pass
        // must produce the same call count — the second run is
        // semantically a no-op against existing rows.
        WritePackage("se", new[] { "a.md" });

        var (sut, bindingService) = CreateSut();

        await sut.ApplySeedsAsync("default", TestContext.Current.CancellationToken);
        await sut.ApplySeedsAsync("default", TestContext.Current.CancellationToken);

        await bindingService.Received(2).BindAsync(
            "se", enabled: true, Arg.Any<CancellationToken>());
    }

    private (FileSystemSkillBundleSeedProvider Provider, ITenantSkillBundleBindingService BindingService) CreateSut(
        ILogger<FileSystemSkillBundleSeedProvider>? logger = null,
        bool rootConfigured = true,
        string? customRoot = null)
    {
        // Build a service provider whose scope resolves the substitute
        // binding service — mirrors the production DI graph where the
        // seed provider opens a child scope per pass and resolves the
        // scoped binding service from it.
        var bindingService = Substitute.For<ITenantSkillBundleBindingService>();
        bindingService
            .BindAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new TenantSkillBundleBinding(
                TenantId: "default",
                BundleId: ci.Arg<string>(),
                Enabled: ci.Arg<bool>(),
                BoundAt: DateTimeOffset.UtcNow)));

        var services = new ServiceCollection();
        services.AddSingleton(bindingService);
        string? rootValue = customRoot ?? (rootConfigured ? _root : null);

        var provider = new FileSystemSkillBundleSeedProvider(
            Options.Create(new SkillBundleOptions { PackagesRoot = rootValue }),
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            logger ?? NullLogger<FileSystemSkillBundleSeedProvider>.Instance);
        return (provider, bindingService);
    }

    private void WritePackage(string packageDir, string[] skillFiles)
    {
        var skillsDir = Path.Combine(_root, packageDir, "skills");
        Directory.CreateDirectory(skillsDir);
        foreach (var skill in skillFiles)
        {
            File.WriteAllText(Path.Combine(skillsDir, skill), "## prompt");
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);
}