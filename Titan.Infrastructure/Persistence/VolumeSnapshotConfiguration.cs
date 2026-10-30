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
        // Incus has no status word for it: existence only (titan-ops architecture/desired-and-current.md#existence-axis).
        builder.Property(s => s.CurrentExistence).HasColumnName("current_existence").HasConversion<string>().IsRequired();
        builder.Property(s => s.DesiredExistence).HasColumnName("desired_existence").HasConversion<string>().IsRequired();
        builder.Property(s => s.PendingIntentId).HasColumnName("pending_intent_id");
        builder.Ignore(s => s.Transition);
        builder.Property(s => s.ExternalId).HasColumnName("external_id");
        builder.Property(s => s.RequestFailureReason).HasColumnName("request_failure_reason");
        builder.Property(s => s.RequestFailureDetail).HasColumnName("request_failure_detail").HasColumnType("text");
        builder.Property(s => s.RequestFailedAt).HasColumnName("request_failed_at");
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(s => s.VolumeId);
        builder.HasIndex(s => s.TenantId);
    }
}
