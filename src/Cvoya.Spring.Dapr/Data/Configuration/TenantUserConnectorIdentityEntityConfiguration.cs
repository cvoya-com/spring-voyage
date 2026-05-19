// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="TenantUserConnectorIdentityEntity"/>
/// (ADR-0047 §2). Owns column shape, the natural-key unique constraint,
/// and the reverse-lookup unique constraint that backs
/// <see cref="Cvoya.Spring.Core.Security.ITenantUserConnectorIdentityResolver.ResolveTenantUserByUsernameAsync"/>.
/// The tenant query filter itself is applied on the DbContext per
/// CONVENTIONS § 12.
/// </summary>
internal class TenantUserConnectorIdentityEntityConfiguration : IEntityTypeConfiguration<TenantUserConnectorIdentityEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<TenantUserConnectorIdentityEntity> builder)
    {
        builder.ToTable("tenant_user_connector_identities");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantUserId).HasColumnName("tenant_user_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.ConnectorId).HasColumnName("connector_id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.Username).HasColumnName("username").IsRequired().HasMaxLength(256);
        builder.Property(e => e.DisplayHandle).HasColumnName("display_handle").HasMaxLength(256);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // Natural key per ADR-0047 §2: one identity per
        // (tenant_user, connector). Re-running "set my GitHub username"
        // upserts in place rather than appending a second row.
        builder.HasIndex(e => new { e.TenantId, e.TenantUserId, e.ConnectorId })
            .IsUnique()
            .HasDatabaseName("ux_tenant_user_connector_identities_tenant_user_connector");

        // Reverse lookup per ADR-0047 §2: "who is connector login X in
        // tenant T?" One connector login maps to at most one tenant
        // user per tenant — including tenant_user_id in the constraint
        // would defeat the resolver's "given a login, who is this?"
        // query (per the comparable rejected shape on the prior
        // HumanConnectorIdentity index design).
        builder.HasIndex(e => new { e.TenantId, e.ConnectorId, e.Username })
            .IsUnique()
            .HasDatabaseName("ux_tenant_user_connector_identities_tenant_connector_username");
    }
}
