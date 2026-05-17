// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using System.Collections.Generic;
using System.Text.Json;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="UnitMembershipHumanEntity"/>
/// (ADR-0044 § 3). Synthetic membership Guid PK; unique index on
/// <c>(tenant_id, unit_id, human_id, role)</c> enforces the set-semantic
/// invariant; secondary non-unique index on <c>(tenant_id, unit_id, human_id)</c>
/// supports "list my roles on this unit" reads. The tenant query filter
/// itself is applied on the DbContext per CONVENTIONS § 12 so it can
/// reference the per-instance <c>CurrentTenantId</c> closure.
/// </summary>
internal class UnitMembershipHumanEntityConfiguration : IEntityTypeConfiguration<UnitMembershipHumanEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UnitMembershipHumanEntity> builder)
    {
        builder.ToTable("unit_memberships_humans");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.UnitId).HasColumnName("unit_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.HumanId).HasColumnName("human_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.Role).HasColumnName("role").IsRequired().HasMaxLength(128);

        builder.Property(e => e.Expertise)
            .HasColumnName("expertise")
            .HasColumnType("jsonb")
            .IsRequired()
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => string.IsNullOrEmpty(v)
                    ? new List<string>()
                    : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
            .Metadata.SetValueComparer(StringListValueComparer);

        builder.Property(e => e.Notifications)
            .HasColumnName("notifications")
            .HasColumnType("jsonb")
            .IsRequired()
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => string.IsNullOrEmpty(v)
                    ? new List<string>()
                    : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
            .Metadata.SetValueComparer(StringListValueComparer);

        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.UnitId, e.HumanId, e.Role })
            .IsUnique()
            .HasDatabaseName("ux_unit_memberships_humans_tenant_unit_human_role");

        builder.HasIndex(e => new { e.TenantId, e.UnitId, e.HumanId })
            .HasDatabaseName("ix_unit_memberships_humans_tenant_unit_human");
    }

    /// <summary>
    /// Ordered-by-value comparer for the jsonb-projected <c>List&lt;string&gt;</c>
    /// columns. Without this EF uses reference equality on the list, which
    /// (a) defeats change tracking on edits in place and (b) emits a noisy
    /// CS warning when the model is built.
    /// </summary>
    private static readonly ValueComparer<List<string>> StringListValueComparer = new(
        (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
        v => v == null ? 0 : v.Aggregate(0, (acc, s) => HashCode.Combine(acc, s)),
        v => v == null ? new List<string>() : v.ToList());
}
