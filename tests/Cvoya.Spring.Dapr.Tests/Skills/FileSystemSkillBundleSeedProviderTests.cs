// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="FileSystemSkillBundleSeedProvider"/>. Verify the
/// adapter is read-only / idempotent, logs every discovered package, and
/// degrades cleanly (warn + return) when the packages root is missing.
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
        var sut = CreateSut();

        sut.Id.ShouldBe("skill-bundles");
        sut.Priority.ShouldBe(10);
    }

    [Fact]
    public async Task ApplySeedsAsync_EnumeratesPackages_And_LogsEachOne()
    {
        WritePackage("software-engineering", new[] { "triage.md", "implement.md" });
        WritePackage("research", new[] { "summarise.md" });

        var logger = new CapturingLogger<FileSystemSkillBundleSeedProvider>();
        var sut = CreateSut(logger);

        await sut.ApplySeedsAsync("default", TestContext.Current.CancellationToken);

        // Two per-package log lines + one summary line.
        var infos = logger.Entries.Where(e => e.Level == LogLevel.Information).ToArray();
        infos.Count(e => e.Message.Contains("software-engineering")).ShouldBe(1);
        infos.Count(e => e.Message.Contains("research")).ShouldBe(1);
        infos.Count(e => e.Message.Contains("enumerated 2 package")).ShouldBe(1);
    }

    [Fact]
    public async Task ApplySeedsAsync_EmitsSkillCounts_PerPackage()
    {
        WritePackage("se", new[] { "a.md", "b.md", "c.md" });

        var logger = new CapturingLogger<FileSystemSkillBundleSeedProvider>();
        var sut = CreateSut(logger);

        await sut.ApplySeedsAsync("default", TestContext.Current.CancellationToken);

        logger.Entries
            .Any(e => e.Message.Contains("'se'") && e.Message.Contains("3 skill"))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task ApplySeedsAsync_NoPackagesRoot_LogsWarningAndReturns()
    {
        var logger = new CapturingLogger<FileSystemSkillBundleSeedProvider>();
        var sut = new FileSystemSkillBundleSeedProvider(
            Options.Create(new SkillBundleOptions { PackagesRoot = null }),
            logger);

        await Should.NotThrowAsync(
            () => sut.ApplySeedsAsync("default", TestContext.Current.CancellationToken));

        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("not configured"));
    }

    [Fact]
    public async Task ApplySeedsAsync_PackagesRootMissing_LogsWarningAndReturns()
    {
        var logger = new CapturingLogger<FileSystemSkillBundleSeedProvider>();
        var sut = new FileSystemSkillBundleSeedProvider(
            Options.Create(new SkillBundleOptions
            {
                PackagesRoot = Path.Combine(_root, "does-not-exist"),
            }),
            logger);

        await Should.NotThrowAsync(
            () => sut.ApplySeedsAsync("default", TestContext.Current.CancellationToken));

        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("does not exist"));
    }

    [Fact]
    public async Task ApplySeedsAsync_TenantIdRequired()
    {
        var sut = CreateSut();

        await Should.ThrowAsync<ArgumentException>(
            () => sut.ApplySeedsAsync(string.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplySeedsAsync_RunTwice_ProducesNoSideEffects()
    {
        // Idempotency for a read-only provider means: rerunning yields the
        // same observable behaviour and never mutates the on-disk layout.
        WritePackage("se", new[] { "a.md" });

        var logger = new CapturingLogger<FileSystemSkillBundleSeedProvider>();
        var sut = CreateSut(logger);

        await sut.ApplySeedsAsync("default", TestContext.Current.CancellationToken);
        var entriesAfterFirst = logger.Entries.Count;

        await sut.ApplySeedsAsync("default", TestContext.Current.CancellationToken);
        var entriesAfterSecond = logger.Entries.Count;

        // Each call emits the same number of entries — no duplicate
        // "wrote new row" signals because there are no rows to write.
        (entriesAfterSecond - entriesAfterFirst).ShouldBe(entriesAfterFirst);

        // On-disk state must not have changed (no row creation, no
        // file writes).
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Count().ShouldBe(1);
    }

    private FileSystemSkillBundleSeedProvider CreateSut(ILogger<FileSystemSkillBundleSeedProvider>? logger = null)
        => new(
            Options.Create(new SkillBundleOptions { PackagesRoot = _root }),
            logger ?? NullLogger<FileSystemSkillBundleSeedProvider>.Instance);

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