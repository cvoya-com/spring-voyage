// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using System.Text.Json;

using Cvoya.Spring.Dapr.Actors;
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
        builder.Property(e => e.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        // ADR-0062 § 1: NOT NULL FK to tenant_users.id. Every Human-insert
        // path stamps a value through ITenantUserDefaultResolver at insert
        // time so the column is never null on the wire. The FK is declared
        // shadow-style (no navigation on HumanEntity to keep the DTO shape
        // narrow); the reverse index supports the inbox-resolver query
        // (where tenant_user_id == caller) and the cloud-overlay's
        // permission key.
        builder.Property(e => e.TenantUserId)
            .HasColumnName("tenant_user_id")
            .IsRequired()
            .HasColumnType("uuid");
        builder.HasIndex(e => e.TenantUserId)
            .HasDatabaseName("ix_humans_tenant_user_id");
        builder.Property(e => e.Username).HasColumnName("username").IsRequired().HasMaxLength(256);
        builder.Property(e => e.DisplayName).HasColumnName("display_name").IsRequired().HasMaxLength(256);
        builder.Property(e => e.Description).HasColumnName("description");
        builder.Property(e => e.Email).HasColumnName("email").HasMaxLength(512);
        builder.Property(e => e.PermissionLevel)
            .HasColumnName("permission_level")
            .IsRequired()
            .HasConversion<int>();
        builder.Property(e => e.NotificationPreferences)
            .HasColumnName("notification_preferences")
            .HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => string.IsNullOrEmpty(v) ? null : JsonSerializer.Deserialize<NotificationPreferences>(v, (JsonSerializerOptions?)null));
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        // Unique username per tenant — the lookup key for every JWT boundary.
        builder.HasIndex(e => new { e.TenantId, e.Username }).IsUnique();
        builder.HasIndex(e => e.TenantId);
    }
}
