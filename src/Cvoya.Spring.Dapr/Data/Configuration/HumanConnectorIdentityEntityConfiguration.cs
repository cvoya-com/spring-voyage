// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="HumanConnectorIdentityEntity"/>
/// (#2408). Owns column shape, indices, and the unique constraint that
/// enforces "one external identity → at most one human per tenant". The
/// tenant query filter itself is applied on the DbContext per
/// CONVENTIONS § 12 so it can reference the per-instance
/// <c>CurrentTenantId</c> closure.
/// </summary>
internal class HumanConnectorIdentityEntityConfiguration : IEntityTypeConfiguration<HumanConnectorIdentityEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<HumanConnectorIdentityEntity> builder)
    {
        builder.ToTable("human_connector_identities");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.HumanId).HasColumnName("human_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.ConnectorId).HasColumnName("connector_id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.ConnectorUserId).HasColumnName("connector_user_id").IsRequired().HasMaxLength(256);
        builder.Property(e => e.DisplayHandle).HasColumnName("display_handle").HasMaxLength(256);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // Unique invariant: one external identity → at most one human per
        // tenant. Including human_id would weaken the constraint and let
        // two humans claim the same login.
        builder.HasIndex(e => new { e.TenantId, e.ConnectorId, e.ConnectorUserId })
            .IsUnique()
            .HasDatabaseName("ux_human_connector_identities_tenant_connector_user");

        // Covering index for "list this human's identities" — the
        // `spring human identity list --human <id>` read pattern.
        builder.HasIndex(e => new { e.TenantId, e.HumanId })
            .HasDatabaseName("ix_human_connector_identities_tenant_human");
    }
}
