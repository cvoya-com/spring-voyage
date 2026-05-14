// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Data;

using System.Text.Json;

using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="UnitConnectorBindingRepository"/> (#2050 / ADR-0040).
/// Exercises the upsert / read paths against the EF in-memory provider —
/// the integration suite covers the same surface against Postgres. The
/// fast tests pin the contract that the singleton store and the
/// <c>UnitActorConnectorConfigStore</c> adapter rely on: rebind
/// upsert-in-place, metadata wipe on rebind, throw on metadata set
/// without binding, idempotent clear, and per-unit isolation.
/// </summary>
public class UnitConnectorBindingRepositoryTests : IDisposable
{
    private static readonly Guid Unit1 = new("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid Unit2 = new("cccccccc-0000-0000-0000-000000000002");

    private static readonly Guid GitHubType = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SlackType = new("22222222-2222-2222-2222-222222222222");

    private readonly SpringDbContext _context;
    private readonly UnitConnectorBindingRepository _repository;

    public UnitConnectorBindingRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new SpringDbContext(options);
        _repository = new UnitConnectorBindingRepository(_context);
    }

    [Fact]
    public async Task GetAsync_NoRow_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var fetched = await _repository.GetAsync(Unit1, ct);
        fetched.ShouldBeNull();
    }

    [Fact]
    public async Task SetAsync_NewBinding_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = JsonSerializer.SerializeToElement(new { owner = "acme", repo = "spring-voyage" });

        await _repository.SetAsync(Unit1, GitHubType, config, ct);

        var fetched = await _repository.GetAsync(Unit1, ct);
        fetched.ShouldNotBeNull();
        fetched.TypeId.ShouldBe(GitHubType);
        fetched.Config.GetProperty("owner").GetString().ShouldBe("acme");
        fetched.Config.GetProperty("repo").GetString().ShouldBe("spring-voyage");
    }

    [Fact]
    public async Task SetAsync_Rebind_UpsertsInPlace_WipesMetadata()
    {
        // Re-binding to a different connector type must not leak the
        // previous connector's runtime metadata to the new connector's
        // teardown path.
        var ct = TestContext.Current.CancellationToken;
        var ghConfig = JsonSerializer.SerializeToElement(new { owner = "acme", repo = "spring-voyage" });
        var slackConfig = JsonSerializer.SerializeToElement(new { workspace = "acme-eng" });
        var ghMetadata = JsonSerializer.SerializeToElement(new { hookId = 12345 });

        await _repository.SetAsync(Unit1, GitHubType, ghConfig, ct);
        await _repository.SetMetadataAsync(Unit1, ghMetadata, ct);

        await _repository.SetAsync(Unit1, SlackType, slackConfig, ct);

        var fetched = await _repository.GetAsync(Unit1, ct);
        fetched.ShouldNotBeNull();
        fetched.TypeId.ShouldBe(SlackType);

        var metadata = await _repository.GetMetadataAsync(Unit1, ct);
        metadata.ShouldBeNull();
    }

    [Fact]
    public async Task ClearAsync_RemovesBindingAndMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = JsonSerializer.SerializeToElement(new { owner = "acme", repo = "spring-voyage" });
        var metadata = JsonSerializer.SerializeToElement(new { hookId = 12345 });

        await _repository.SetAsync(Unit1, GitHubType, config, ct);
        await _repository.SetMetadataAsync(Unit1, metadata, ct);

        await _repository.ClearAsync(Unit1, ct);

        (await _repository.GetAsync(Unit1, ct)).ShouldBeNull();
        (await _repository.GetMetadataAsync(Unit1, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ClearAsync_NoBinding_NoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        await Should.NotThrowAsync(async () => await _repository.ClearAsync(Unit1, ct));
    }

    [Fact]
    public async Task GetMetadataAsync_BoundButNoMetadata_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = JsonSerializer.SerializeToElement(new { owner = "acme", repo = "spring-voyage" });

        await _repository.SetAsync(Unit1, GitHubType, config, ct);

        var metadata = await _repository.GetMetadataAsync(Unit1, ct);
        metadata.ShouldBeNull();
    }

    [Fact]
    public async Task SetMetadataAsync_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = JsonSerializer.SerializeToElement(new { owner = "acme", repo = "spring-voyage" });
        var metadata = JsonSerializer.SerializeToElement(new { hookId = 12345 });

        await _repository.SetAsync(Unit1, GitHubType, config, ct);
        await _repository.SetMetadataAsync(Unit1, metadata, ct);

        var fetched = await _repository.GetMetadataAsync(Unit1, ct);
        fetched.ShouldNotBeNull();
        fetched.Value.GetProperty("hookId").GetInt32().ShouldBe(12345);
    }

    [Fact]
    public async Task SetMetadataAsync_NoBinding_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var metadata = JsonSerializer.SerializeToElement(new { hookId = 12345 });

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await _repository.SetMetadataAsync(Unit1, metadata, ct));
    }

    [Fact]
    public async Task ClearMetadataAsync_LeavesBindingIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = JsonSerializer.SerializeToElement(new { owner = "acme", repo = "spring-voyage" });
        var metadata = JsonSerializer.SerializeToElement(new { hookId = 12345 });

        await _repository.SetAsync(Unit1, GitHubType, config, ct);
        await _repository.SetMetadataAsync(Unit1, metadata, ct);

        await _repository.ClearMetadataAsync(Unit1, ct);

        (await _repository.GetMetadataAsync(Unit1, ct)).ShouldBeNull();
        var binding = await _repository.GetAsync(Unit1, ct);
        binding.ShouldNotBeNull();
        binding.TypeId.ShouldBe(GitHubType);
    }

    [Fact]
    public async Task ClearMetadataAsync_NoBinding_NoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        await Should.NotThrowAsync(async () => await _repository.ClearMetadataAsync(Unit1, ct));
    }

    [Fact]
    public async Task PerUnit_DoesNotLeakAcrossUnits()
    {
        var ct = TestContext.Current.CancellationToken;
        var ghConfig = JsonSerializer.SerializeToElement(new { owner = "acme", repo = "spring-voyage" });
        var slackConfig = JsonSerializer.SerializeToElement(new { workspace = "acme-eng" });

        await _repository.SetAsync(Unit1, GitHubType, ghConfig, ct);
        await _repository.SetAsync(Unit2, SlackType, slackConfig, ct);

        var a = await _repository.GetAsync(Unit1, ct);
        var b = await _repository.GetAsync(Unit2, ct);

        a.ShouldNotBeNull();
        b.ShouldNotBeNull();
        a.TypeId.ShouldBe(GitHubType);
        b.TypeId.ShouldBe(SlackType);
    }

    [Fact]
    public async Task BindingSurvivesAcrossDbContextInstances()
    {
        // Cross-restart proxy: each repository instance gets its own
        // DbContext, simulating actor reactivation reading the same row.
        var ct = TestContext.Current.CancellationToken;
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var ghConfig = JsonSerializer.SerializeToElement(new { owner = "acme", repo = "spring-voyage" });
        var ghMetadata = JsonSerializer.SerializeToElement(new { hookId = 12345 });

        await using (var write = new SpringDbContext(options))
        {
            var writer = new UnitConnectorBindingRepository(write);
            await writer.SetAsync(Unit1, GitHubType, ghConfig, ct);
            await writer.SetMetadataAsync(Unit1, ghMetadata, ct);
        }

        await using (var read = new SpringDbContext(options))
        {
            var reader = new UnitConnectorBindingRepository(read);
            var binding = await reader.GetAsync(Unit1, ct);
            binding.ShouldNotBeNull();
            binding.TypeId.ShouldBe(GitHubType);
            binding.Config.GetProperty("owner").GetString().ShouldBe("acme");

            var metadata = await reader.GetMetadataAsync(Unit1, ct);
            metadata.ShouldNotBeNull();
            metadata.Value.GetProperty("hookId").GetInt32().ShouldBe(12345);
        }
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
