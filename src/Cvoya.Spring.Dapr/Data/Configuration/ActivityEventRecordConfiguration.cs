// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the <see cref="ActivityEventRecord"/> type.
/// Applies snake_case naming, tenant scoping, and indexes for querying.
/// Activity events have no soft-delete column; the tenant query filter
/// is applied on the DbContext.
/// </summary>
internal class ActivityEventRecordConfiguration : IEntityTypeConfiguration<ActivityEventRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ActivityEventRecord> builder)
    {
        builder.ToTable("activity_events");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").HasColumnType("uuid");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.SourceId).HasColumnName("source_id").IsRequired().HasColumnType("uuid");
        builder.Property(e => e.EventType).HasColumnName("event_type").IsRequired().HasMaxLength(128);
        builder.Property(e => e.Severity).HasColumnName("severity").IsRequired().HasMaxLength(32);
        builder.Property(e => e.Summary).HasColumnName("summary").IsRequired().HasMaxLength(1024);
        builder.Property(e => e.Details).HasColumnName("details").HasColumnType("jsonb");
        builder.Property(e => e.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128);
        builder.Property(e => e.Cost).HasColumnName("cost").HasPrecision(18, 6);
        builder.Property(e => e.Timestamp).HasColumnName("timestamp").IsRequired();

        builder.HasIndex(e => e.Timestamp);
        builder.HasIndex(e => e.CorrelationId);
        builder.HasIndex(e => e.TenantId);
    }
}