using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.VolumeOffering;
using Titan.Domain.Aggregates.Volume;

namespace Titan.Infrastructure.Persistence;

public class VolumeConfiguration : IEntityTypeConfiguration<Volume>
{
    public void Configure(EntityTypeBuilder<Volume> builder)
    {
        builder.ToTable("volumes");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).HasColumnName("id");
        builder.Property(v => v.TenantId).HasColumnName("tenant_id");
        builder.Property(v => v.Name).HasColumnName("name").IsRequired().HasMaxLength(100);
        builder.Property(v => v.ClusterId).HasColumnName("cluster_id").IsRequired();
        builder.Property(v => v.VolumeOfferingId).HasColumnName("volume_offering_id").IsRequired();
        // titan-ops features/catalog/design.md#resources-reference-their-offering. No navigation: Compute holds no Catalog type.
        builder.HasOne<VolumeOffering>()
            .WithMany()
            .HasForeignKey(v => v.VolumeOfferingId)
            .HasConstraintName("FK_volumes_volume_offerings_volume_offering_id")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(v => v.SizeGb).HasColumnName("size_gb");
        // titan-ops features/volume/design.md#volume-state-is-existence: status is the one status. The volume's own waiting
        // operation and last failure are a value object kept in this table's columns, never null, with observed_state
        // whether Incus was last seen holding the volume while an operation waits (titan-ops features/volume/design.md#volume-data-model).
        builder.Property(v => v.Status).HasColumnName("status").HasConversion<string>().IsRequired();
        builder.OwnsOne(v => v.Operation, o =>
        {
            o.Property(x => x.PendingId).HasColumnName("pending_request_id");
            o.Property(x => x.AskedAt).HasColumnName("pending_request_asked_at");
            o.Property(x => x.FailureReason).HasColumnName("request_failure_reason");
            o.Property(x => x.FailureDetail).HasColumnName("request_failure_detail").HasColumnType("text");
            o.Property(x => x.FailedAt).HasColumnName("request_failed_at");
            o.Property(x => x.LastSeenStatus).HasColumnName("observed_state").HasConversion<string>();
        });
        builder.Ignore(v => v.InstanceStatus);
        builder.Property(v => v.ExternalId).HasColumnName("external_id");
        // The attachment pair: Incus's used_by (only observations) and the customer's ask (only requests).
        builder.Property(v => v.CurrentAttachedVmId).HasColumnName("current_attached_vm_id");
        builder.Property(v => v.CurrentAttachedElsewhere).HasColumnName("current_attached_elsewhere");
        builder.Property(v => v.DesiredAttachedVmId).HasColumnName("desired_attached_vm_id");
        builder.Property(v => v.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(v => v.TenantId);

        // The snapshots are the volume's (titan-ops architecture/code-structure.md#child-entity): loaded and saved with it,
        // through the backing field. Restrict, not cascade: no row of either table is ever deleted.
        builder.HasMany(v => v.Snapshots)
            .WithOne()
            .HasForeignKey(s => s.VolumeId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(v => v.Snapshots).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(v => v.SnapshotQuotaUsage);
    }
}
