// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the <see cref="UserEntity"/> type.
/// Applies snake_case naming and audit columns.
/// </summary>
internal class UserEntityConfiguration : IEntityTypeConfiguration<UserEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UserEntity> builder)
    {
        builder.ToTable("users");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.GitHubId).HasColumnName("github_id").IsRequired().HasMaxLength(64);
        builder.Property(e => e.GitHubLogin).HasColumnName("github_login").IsRequired().HasMaxLength(256);
        builder.Property(e => e.DisplayName).HasColumnName("display_name").IsRequired().HasMaxLength(256);
        builder.Property(e => e.Email).HasColumnName("email").HasMaxLength(256);
        builder.Property(e => e.AvatarUrl).HasColumnName("avatar_url").HasMaxLength(1024);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(e => e.GitHubId).IsUnique();
        builder.HasIndex(e => e.GitHubLogin);
    }
}
