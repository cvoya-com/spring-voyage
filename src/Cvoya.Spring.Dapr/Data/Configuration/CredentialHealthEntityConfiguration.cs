// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the <see cref="CredentialHealthEntity"/>
/// type. Composite PK <c>(tenant_id, kind, subject_id, secret_name)</c>
/// so every (runtime-or-connector, credential) pair has exactly one
/// current-state row. No soft-delete — credential-health rows represent
/// operational state, not business data; uninstalling the subject
/// removes its credential-health row too (handled by the install
/// service's uninstall path).
/// </summary>
internal class CredentialHealthEntityConfiguration : IEntityTypeConfiguration<CredentialHealthEntity>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CredentialHealthEntity> builder)
    {
        builder.ToTable("credential_health");

        builder.HasKey(e => new { e.TenantId, e.Kind, e.SubjectId, e.SecretName });
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.Kind).HasColumnName("kind").IsRequired().HasConversion<int>();
        builder.Property(e => e.SubjectId).HasColumnName("subject_id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.SecretName).HasColumnName("secret_name").IsRequired().HasMaxLength(128);
        builder.Property(e => e.Status).HasColumnName("status").IsRequired().HasConversion<int>();
        builder.Property(e => e.LastError).HasColumnName("last_error").HasMaxLength(2048);
        builder.Property(e => e.LastChecked).HasColumnName("last_checked").IsRequired();

        builder.HasIndex(e => e.TenantId);
        builder.HasIndex(e => new { e.TenantId, e.Kind });
    }
}