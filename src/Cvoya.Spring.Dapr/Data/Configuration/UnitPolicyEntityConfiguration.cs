// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="UnitPolicyEntity"/>. Keyed on
/// <c>unit_id</c>; sub-record dimensions are stored as jsonb columns so
/// adding new dimensions is additive.
/// </summary>
internal class UnitPolicyEntityConfiguration : IEntityTypeConfiguration<UnitPolicyEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UnitPolicyEntity> builder)
    {
        builder.ToTable("unit_policies");

        builder.HasKey(e => e.UnitId);

        builder.Property(e => e.UnitId).HasColumnName("unit_id").IsRequired().HasMaxLength(256);
        builder.Property(e => e.Skill).HasColumnName("skill").HasColumnType("jsonb");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
    }
}