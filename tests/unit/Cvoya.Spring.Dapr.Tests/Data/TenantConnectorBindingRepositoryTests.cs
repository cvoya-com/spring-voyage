// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Data;

using System.Text.Json;

using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="TenantConnectorBindingRepository"/> (ADR-0061
/// §1). Mirrors <see cref="UnitConnectorBindingRepositoryTests"/>: the
/// fast tests pin the contract that the singleton store and the
/// per-tenant binding endpoints rely on — rebind upsert-in-place,
/// metadata wipe on rebind, throw on metadata set without binding,
/// idempotent clear, and per-slug isolation.
/// </summary>
public class TenantConnectorBindingRepositoryTests : IDisposable
{
    private const string SlackSlug = "slack";
    private const string OtherSlug = "calendar";

    private static readonly Guid SlackType = new("11111111-2222-3333-4444-000000000001");
    private static readonly Guid CalendarType = new("11111111-2222-3333-4444-000000000002");

    private readonly SpringDbContext _context;
    private readonly TenantConnectorBindingRepository _repository;

    public TenantConnectorBindingRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new SpringDbContext(options);
        _repository = new TenantConnectorBindingRepository(_context);
    }

    [Fact]
    public async Task GetAsync_NoRow_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var fetched = await _repository.GetAsync(SlackSlug, ct);
        fetched.ShouldBeNull();
    }

    [Fact]
    public async Task SetAsync_NewBinding_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = JsonSerializer.SerializeToElement(new
        {
            team_id = "T123",
            bot_user_id = "U999",
        });

        await _repository.SetAsync(SlackSlug, SlackType, config, externalIdentity: "T123", ct);

        var fetched = await _repository.GetAsync(SlackSlug, ct);
        fetched.ShouldNotBeNull();
        fetched.ConnectorSlug.ShouldBe(SlackSlug);
        fetched.TypeId.ShouldBe(SlackType);
        fetched.ExternalIdentity.ShouldBe("T123");
        fetched.Config.GetProperty("team_id").GetString().ShouldBe("T123");
        fetched.Config.GetProperty("bot_user_id").GetString().ShouldBe("U999");
    }

    [Fact]
    public async Task GetByExternalIdentityAsync_Hit_ReturnsBinding()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = JsonSerializer.SerializeToElement(new { team_id = "T-ext-hit" });

        await _repository.SetAsync(SlackSlug, SlackType, config, externalIdentity: "T-ext-hit", ct);

        var fetched = await _repository.GetByExternalIdentityAsync(SlackSlug, "T-ext-hit", ct);
        fetched.ShouldNotBeNull();
        fetched.ConnectorSlug.ShouldBe(SlackSlug);
        fetched.TypeId.ShouldBe(SlackType);
        fetched.ExternalIdentity.ShouldBe("T-ext-hit");
    }

    [Fact]
    public async Task GetByExternalIdentityAsync_Miss_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var fetched = await _repository.GetByExternalIdentityAsync(SlackSlug, "never-bound", ct);
        fetched.ShouldBeNull();
    }

    [Fact]
    public async Task GetByExternalIdentityAsync_Hit_IsTenantAgnostic()
    {
        // The cross-tenant resolver does not require an ambient tenant
        // context — the inbound webhook hasn't resolved one yet. With
        // the InMemory provider's query-filter behaviour, IgnoreQueryFilters
        // is what makes the call work for a tenant the writer never set.
        // Inserting under the default tenant and reading under a fresh
        // context backed by the same database name proves the bypass.
        var ct = TestContext.Current.CancellationToken;
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var config = JsonSerializer.SerializeToElement(new { team_id = "T-cross" });

        await using (var write = new SpringDbContext(options))
        {
            var writer = new TenantConnectorBindingRepository(write);
            await writer.SetAsync(SlackSlug, SlackType, config, externalIdentity: "T-cross", ct);
        }

        await using (var read = new SpringDbContext(options))
        {
            var reader = new TenantConnectorBindingRepository(read);
            var binding = await reader.GetByExternalIdentityAsync(SlackSlug, "T-cross", ct);
            binding.ShouldNotBeNull();
            binding.ExternalIdentity.ShouldBe("T-cross");
        }
    }

    [Fact]
    public async Task SetAsync_Rebind_UpsertsInPlace_WipesMetadata()
    {
        // Re-binding to the same slug (e.g. fresh OAuth install) must
        // not leak the previous install's runtime metadata.
        var ct = TestContext.Current.CancellationToken;
        var config1 = JsonSerializer.SerializeToElement(new { team_id = "T123" });
        var config2 = JsonSerializer.SerializeToElement(new { team_id = "T456" });
        var metadata1 = JsonSerializer.SerializeToElement(new { auth_revoke_attempts = 1 });

        await _repository.SetAsync(SlackSlug, SlackType, config1, externalIdentity: "T123", ct);
        await _repository.SetMetadataAsync(SlackSlug, metadata1, ct);

        await _repository.SetAsync(SlackSlug, SlackType, config2, externalIdentity: "T456", ct);

        var fetched = await _repository.GetAsync(SlackSlug, ct);
        fetched.ShouldNotBeNull();
        fetched.Config.GetProperty("team_id").GetString().ShouldBe("T456");

        var metadata = await _repository.GetMetadataAsync(SlackSlug, ct);
        metadata.ShouldBeNull();
    }

    [Fact]
    public async Task ClearAsync_RemovesBindingAndMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = JsonSerializer.SerializeToElement(new { team_id = "T123" });
        var metadata = JsonSerializer.SerializeToElement(new { revoke_attempts = 0 });

        await _repository.SetAsync(SlackSlug, SlackType, config, externalIdentity: null, ct);
        await _repository.SetMetadataAsync(SlackSlug, metadata, ct);

        await _repository.ClearAsync(SlackSlug, ct);

        (await _repository.GetAsync(SlackSlug, ct)).ShouldBeNull();
        (await _repository.GetMetadataAsync(SlackSlug, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ClearAsync_NoBinding_NoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        await Should.NotThrowAsync(async () => await _repository.ClearAsync(SlackSlug, ct));
    }

    [Fact]
    public async Task GetMetadataAsync_BoundButNoMetadata_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = JsonSerializer.SerializeToElement(new { team_id = "T123" });

        await _repository.SetAsync(SlackSlug, SlackType, config, externalIdentity: null, ct);

        var metadata = await _repository.GetMetadataAsync(SlackSlug, ct);
        metadata.ShouldBeNull();
    }

    [Fact]
    public async Task SetMetadataAsync_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = JsonSerializer.SerializeToElement(new { team_id = "T123" });
        var metadata = JsonSerializer.SerializeToElement(new { revoke_attempts = 2 });

        await _repository.SetAsync(SlackSlug, SlackType, config, externalIdentity: null, ct);
        await _repository.SetMetadataAsync(SlackSlug, metadata, ct);

        var fetched = await _repository.GetMetadataAsync(SlackSlug, ct);
        fetched.ShouldNotBeNull();
        fetched.Value.GetProperty("revoke_attempts").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task SetMetadataAsync_NoBinding_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var metadata = JsonSerializer.SerializeToElement(new { revoke_attempts = 1 });

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await _repository.SetMetadataAsync(SlackSlug, metadata, ct));
    }

    [Fact]
    public async Task ClearMetadataAsync_LeavesBindingIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = JsonSerializer.SerializeToElement(new { team_id = "T123" });
        var metadata = JsonSerializer.SerializeToElement(new { revoke_attempts = 1 });

        await _repository.SetAsync(SlackSlug, SlackType, config, externalIdentity: null, ct);
        await _repository.SetMetadataAsync(SlackSlug, metadata, ct);

        await _repository.ClearMetadataAsync(SlackSlug, ct);

        (await _repository.GetMetadataAsync(SlackSlug, ct)).ShouldBeNull();
        var binding = await _repository.GetAsync(SlackSlug, ct);
        binding.ShouldNotBeNull();
        binding.TypeId.ShouldBe(SlackType);
    }

    [Fact]
    public async Task ClearMetadataAsync_NoBinding_NoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        await Should.NotThrowAsync(async () => await _repository.ClearMetadataAsync(SlackSlug, ct));
    }

    [Fact]
    public async Task PerSlug_DoesNotLeakAcrossSlugs()
    {
        var ct = TestContext.Current.CancellationToken;
        var slackConfig = JsonSerializer.SerializeToElement(new { team_id = "T123" });
        var calendarConfig = JsonSerializer.SerializeToElement(new { calendar_id = "primary" });

        await _repository.SetAsync(SlackSlug, SlackType, slackConfig, externalIdentity: null, ct);
        await _repository.SetAsync(OtherSlug, CalendarType, calendarConfig, externalIdentity: null, ct);

        var slack = await _repository.GetAsync(SlackSlug, ct);
        var calendar = await _repository.GetAsync(OtherSlug, ct);

        slack.ShouldNotBeNull();
        calendar.ShouldNotBeNull();
        slack.TypeId.ShouldBe(SlackType);
        calendar.TypeId.ShouldBe(CalendarType);
    }

    [Fact]
    public async Task BindingSurvivesAcrossDbContextInstances()
    {
        // Cross-restart proxy: each repository instance gets its own
        // DbContext.
        var ct = TestContext.Current.CancellationToken;
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var slackConfig = JsonSerializer.SerializeToElement(new { team_id = "T123" });
        var metadata = JsonSerializer.SerializeToElement(new { revoke_attempts = 1 });

        await using (var write = new SpringDbContext(options))
        {
            var writer = new TenantConnectorBindingRepository(write);
            await writer.SetAsync(SlackSlug, SlackType, slackConfig, externalIdentity: null, ct);
            await writer.SetMetadataAsync(SlackSlug, metadata, ct);
        }

        await using (var read = new SpringDbContext(options))
        {
            var reader = new TenantConnectorBindingRepository(read);
            var binding = await reader.GetAsync(SlackSlug, ct);
            binding.ShouldNotBeNull();
            binding.TypeId.ShouldBe(SlackType);
            binding.Config.GetProperty("team_id").GetString().ShouldBe("T123");

            var meta = await reader.GetMetadataAsync(SlackSlug, ct);
            meta.ShouldNotBeNull();
            meta.Value.GetProperty("revoke_attempts").GetInt32().ShouldBe(1);
        }
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
