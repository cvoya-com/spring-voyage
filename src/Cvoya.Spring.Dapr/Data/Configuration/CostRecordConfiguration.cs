// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Configuration;

using Cvoya.Spring.Dapr.Costs;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// EF Core configuration for the <see cref="CostRecord"/> type.
/// Applies snake_case naming and indexes for querying by agent, unit,
/// tenant, and timestamp. The tenant query filter is applied on the
/// DbContext.
/// </summary>
internal class CostRecordConfiguration : IEntityTypeConfiguration<CostRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CostRecord> builder)
    {
        builder.ToTable("cost_records");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.AgentId).HasColumnName("agent_id").IsRequired().HasMaxLength(128);
        builder.Property(e => e.UnitId).HasColumnName("unit_id").HasMaxLength(128);
        builder.Property(e => e.Model).HasColumnName("model").IsRequired().HasMaxLength(128);
        builder.Property(e => e.InputTokens).HasColumnName("input_tokens");
        builder.Property(e => e.OutputTokens).HasColumnName("output_tokens");
        builder.Property(e => e.Cost).HasColumnName("cost").HasPrecision(18, 8);
        builder.Property(e => e.Duration).HasColumnName("duration");
        builder.Property(e => e.Timestamp).HasColumnName("timestamp").IsRequired();
        builder.Property(e => e.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128);
        builder.Property(e => e.Source)
            .HasColumnName("source")
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(Cvoya.Spring.Core.Costs.CostSource.Work);

        builder.HasIndex(e => e.AgentId);
        builder.HasIndex(e => e.UnitId);
        builder.HasIndex(e => e.TenantId);
        builder.HasIndex(e => e.Timestamp);
    }
}