// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="HumanEntity"/>. Applies snake_case
/// naming, the tenant column, and a unique index on (tenant_id, username) so
/// username → UUID resolution is O(1) and usernames cannot collide within a
/// tenant. The combined tenant + (no soft-delete) query filter is applied on
/// the DbContext; this configuration owns columns, indices, and PK.
/// </summary>
internal class HumanEntityConfiguration : IEntityTypeConfiguration<HumanEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<HumanEntity> builder)
    {
        builder.ToTable("humans");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.Username).HasColumnName("username").IsRequired().HasMaxLength(256);
        builder.Property(e => e.DisplayName).HasColumnName("display_name").IsRequired().HasMaxLength(256);
        builder.Property(e => e.Email).HasColumnName("email").HasMaxLength(512);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        // Unique username per tenant — the lookup key for every JWT boundary.
        builder.HasIndex(e => new { e.TenantId, e.Username }).IsUnique();
        builder.HasIndex(e => e.TenantId);
    }
}