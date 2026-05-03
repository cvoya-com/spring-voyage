// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the <see cref="ApiTokenEntity"/> type.
/// Applies snake_case naming, audit columns, and the tenant column +
/// index. API tokens have no soft-delete column; revocation is recorded
/// via <c>RevokedAt</c> but rows remain readable. The tenant query
/// filter is applied on the DbContext.
/// </summary>
internal class ApiTokenEntityConfiguration : IEntityTypeConfiguration<ApiTokenEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ApiTokenEntity> builder)
    {
        builder.ToTable("api_tokens");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.UserId).HasColumnName("user_id").HasColumnType("uuid");
        builder.Property(e => e.TokenHash).HasColumnName("token_hash").IsRequired().HasMaxLength(512);
        builder.Property(e => e.Name).HasColumnName("name").IsRequired().HasMaxLength(256);
        builder.Property(e => e.Scopes).HasColumnName("scopes").HasMaxLength(1024);
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.RevokedAt).HasColumnName("revoked_at");

        builder.HasIndex(e => e.TokenHash).IsUnique();
        builder.HasIndex(e => e.TenantId);
    }
}