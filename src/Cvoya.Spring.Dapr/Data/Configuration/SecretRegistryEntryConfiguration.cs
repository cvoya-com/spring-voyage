// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="SecretRegistryEntry"/>. Applies
/// snake_case naming and the structural uniqueness constraint. The
/// tenant query filter is applied on the DbContext.
///
/// <para>
/// The uniqueness constraint spans
/// <c>(TenantId, Scope, OwnerId, Name, Version)</c>; each version of a
/// secret is a separate row, and rotations append without replacing.
/// </para>
/// </summary>
internal class SecretRegistryEntryConfiguration : IEntityTypeConfiguration<SecretRegistryEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SecretRegistryEntry> builder)
    {
        builder.ToTable("secret_registry_entries");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.Scope).HasColumnName("scope").IsRequired().HasConversion<int>();
        builder.Property(e => e.OwnerId).HasColumnName("owner_id").HasColumnType("uuid");
        builder.Property(e => e.Name).HasColumnName("name").IsRequired().HasMaxLength(256);
        builder.Property(e => e.StoreKey).HasColumnName("store_key").IsRequired().HasMaxLength(512);
        builder.Property(e => e.Origin).HasColumnName("origin").IsRequired().HasConversion<int>();
        builder.Property(e => e.Version).HasColumnName("version").IsRequired(false);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.Scope, e.OwnerId, e.Name, e.Version })
            .IsUnique()
            .HasDatabaseName("ix_secret_registry_tenant_scope_owner_name_version");

        builder.HasIndex(e => new { e.TenantId, e.Scope, e.OwnerId, e.Name })
            .HasDatabaseName("ix_secret_registry_tenant_scope_owner_name");
    }
}
