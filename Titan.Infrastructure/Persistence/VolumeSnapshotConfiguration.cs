using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Volume;

namespace Titan.Infrastructure.Persistence;

public class VolumeSnapshotConfiguration : IEntityTypeConfiguration<VolumeSnapshot>
{
    public void Configure(EntityTypeBuilder<VolumeSnapshot> builder)
    {
        builder.ToTable("volume_snapshots");
        builder.HasKey(s => s.Id);
        // The aggregate sets the id. Left "generated on add", EF reads a snapshot added to a tracked volume as one
        // already saved and updates it: 0 rows (same trap as LoadBalancerListener and FirewallRule).
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.VolumeId).HasColumnName("volume_id");
        builder.Property(s => s.TenantId).HasColumnName("tenant_id");
        builder.Property(s => s.Name).HasColumnName("name").IsRequired().HasMaxLength(100);
        // titan-ops features/volume/design.md#volume-snapshot: status is the one status. The snapshot's own waiting operation
        // and last failure are a value object kept in this table's columns, never null, with observed_state whether Incus
        // was last seen holding the snapshot while an operation waits (titan-ops features/volume/design.md#volume-data-model).
        builder.Property(s => s.Status).HasColumnName("status").HasConversion<string>().IsRequired();
        builder.OwnsOne(s => s.Operation, o =>
        {
            o.Property(x => x.PendingId).HasColumnName("pending_request_id");
            o.Property(x => x.AskedAt).HasColumnName("pending_request_asked_at");
            o.Property(x => x.FailureReason).HasColumnName("request_failure_reason");
            o.Property(x => x.FailureDetail).HasColumnName("request_failure_detail").HasColumnType("text");
            o.Property(x => x.FailedAt).HasColumnName("request_failed_at");
            o.Property(x => x.LastSeenStatus).HasColumnName("observed_state").HasConversion<string>();
        });
        builder.Ignore(s => s.InstanceStatus);
        builder.Property(s => s.ExternalId).HasColumnName("external_id");
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(s => s.VolumeId);
        builder.HasIndex(s => s.TenantId);
    }
}
