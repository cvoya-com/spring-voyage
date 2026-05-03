// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for <see cref="PackageInstallEntity"/> — the
/// per-package row in the <c>package_installs</c> table. Multiple rows
/// can share the same <c>install_id</c> (one per package in a multi-package
/// batch). The combined tenant query filter is applied on the DbContext itself.
/// </summary>
internal class PackageInstallEntityConfiguration : IEntityTypeConfiguration<PackageInstallEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PackageInstallEntity> builder)
    {
        builder.ToTable("package_installs");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(e => e.InstallId).HasColumnName("install_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.PackageName).HasColumnName("package_name").IsRequired().HasMaxLength(256);
        builder.Property(e => e.Status)
            .HasColumnName("status")
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);
        builder.Property(e => e.OriginalManifestYaml).HasColumnName("original_manifest_yaml").IsRequired();
        builder.Property(e => e.InputsJson).HasColumnName("inputs_json").IsRequired();
        builder.Property(e => e.PackageRoot).HasColumnName("package_root");
        builder.Property(e => e.StartedAt).HasColumnName("started_at").IsRequired();
        builder.Property(e => e.CompletedAt).HasColumnName("completed_at");
        builder.Property(e => e.ErrorMessage).HasColumnName("error_message");

        builder.HasIndex(e => new { e.TenantId, e.InstallId });
        builder.HasIndex(e => e.TenantId);
    }
}