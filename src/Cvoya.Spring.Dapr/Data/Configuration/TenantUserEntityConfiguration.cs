// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="TenantUserEntity"/> (ADR-0047 §1).
/// Owns column shape and the natural-key unique index
/// <c>(tenant_id, auth_subject)</c>. The tenant query filter itself is
/// applied on the DbContext per CONVENTIONS § 12 so it can reference the
/// per-instance <c>CurrentTenantId</c> closure.
/// </summary>
internal class TenantUserEntityConfiguration : IEntityTypeConfiguration<TenantUserEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TenantUserEntity> builder)
    {
        builder.ToTable("tenant_users");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        // Nullable: OSS dev installs pin the operator row by its
        // deterministic UUID without an OAuth subject claim.
        builder.Property(e => e.AuthSubject).HasColumnName("auth_subject").HasMaxLength(512);
        builder.Property(e => e.DisplayName).HasColumnName("display_name").IsRequired().HasMaxLength(256);
        builder.Property(e => e.Description).HasColumnName("description").HasColumnType("text");
        // ADR-0062 § 2: optional FK to humans.id pinning the "primary"
        // Hat for new outbound. Nullable so a freshly seeded TenantUser
        // can exist before any Human is bound. The FK is intentionally
        // declared as a plain Guid column (not a navigation) to keep the
        // entity shape narrow — the relation is many-to-one Human→TenantUser
        // with the FK on `humans`, and PrimaryHumanId is an orthogonal
        // pointer to the user's preferred sender, not the inverse of the
        // many-to-one binding.
        builder.Property(e => e.PrimaryHumanId)
            .HasColumnName("primary_human_id")
            .HasColumnType("uuid");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // Natural key per ADR-0047 §1: (tenant, auth_subject). Made
        // unique with a partial-index filter on auth_subject IS NOT NULL
        // so OSS rows pinned by deterministic UUID (auth_subject NULL)
        // don't collide on a single "all-null" slot. Reverse lookup
        // "given an OAuth sub, find the tenant user" reads this index.
        builder.HasIndex(e => new { e.TenantId, e.AuthSubject })
            .IsUnique()
            .HasFilter("auth_subject IS NOT NULL")
            .HasDatabaseName("ux_tenant_users_tenant_auth_subject");
    }
}
