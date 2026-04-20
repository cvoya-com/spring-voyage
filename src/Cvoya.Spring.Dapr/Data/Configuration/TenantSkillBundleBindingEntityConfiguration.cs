// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the <see cref="TenantSkillBundleBindingEntity"/>
/// type. Composite PK <c>(tenant_id, bundle_id)</c>; no soft-delete —
/// the <c>enabled</c> bit is the retract signal.
/// </summary>
internal class TenantSkillBundleBindingEntityConfiguration : IEntityTypeConfiguration<TenantSkillBundleBindingEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TenantSkillBundleBindingEntity> builder)
    {
        builder.ToTable("tenant_skill_bundle_bindings");

        builder.HasKey(e => new { e.TenantId, e.BundleId });
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.BundleId).HasColumnName("bundle_id").IsRequired().HasMaxLength(256);
        builder.Property(e => e.Enabled).HasColumnName("enabled").IsRequired();
        builder.Property(e => e.BoundAt).HasColumnName("bound_at").IsRequired();

        builder.HasIndex(e => e.TenantId);
    }
}