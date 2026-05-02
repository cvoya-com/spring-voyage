// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the <see cref="UnitDefinitionEntity"/> type.
/// Applies snake_case naming, audit columns, and the tenant column +
/// index. The combined tenant + soft-delete query filter is applied on
/// the DbContext itself so the closure captures <c>this</c>.
/// </summary>
internal class UnitDefinitionEntityConfiguration : IEntityTypeConfiguration<UnitDefinitionEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UnitDefinitionEntity> builder)
    {
        builder.ToTable("unit_definitions");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.UnitId).HasColumnName("unit_id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.ActorId).HasColumnName("actor_id").HasMaxLength(256);
        builder.Property(e => e.Name).HasColumnName("name").IsRequired().HasMaxLength(256);
        builder.Property(e => e.Description).HasColumnName("description").HasMaxLength(1024);
        builder.Property(e => e.Definition).HasColumnName("definition").HasColumnType("jsonb");
        builder.Property(e => e.Members).HasColumnName("members").HasColumnType("jsonb");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");
        builder.Property(e => e.IsTopLevel)
            .HasColumnName("is_top_level")
            .IsRequired()
            .HasDefaultValue(false);
        builder.Property(e => e.LastValidationErrorJson).HasColumnName("last_validation_error_json");
        builder.Property(e => e.LastValidationRunId).HasColumnName("last_validation_run_id");
        builder.Property(e => e.InstallState)
            .HasColumnName("install_state")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(PackageInstallState.Active);
        builder.Property(e => e.InstallId).HasColumnName("install_id");

        builder.HasIndex(e => new { e.TenantId, e.UnitId }).IsUnique().HasFilter("deleted_at IS NULL");
        builder.HasIndex(e => e.TenantId);
    }
}