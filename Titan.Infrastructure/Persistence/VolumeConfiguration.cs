using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Catalog.VolumeOffering;
using Titan.Domain.Aggregates.Compute.Volume;

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
        // https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#resources-reference-their-offering. No navigation: Compute holds no Catalog type.
        builder.HasOne<VolumeOffering>()
            .WithMany()
            .HasForeignKey(v => v.VolumeOfferingId)
            .HasConstraintName("FK_volumes_volume_offerings_volume_offering_id")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(v => v.SizeGb).HasColumnName("size_gb");
        // Incus has no status word for it: existence only (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#existence-axis).
        builder.Property(v => v.CurrentExistence).HasColumnName("current_existence").HasConversion<string>().IsRequired();
        builder.Property(v => v.DesiredExistence).HasColumnName("desired_existence").HasConversion<string>().IsRequired();
        builder.Property(v => v.PendingIntentId).HasColumnName("pending_intent_id");
        builder.Ignore(v => v.Transition);
        builder.Property(v => v.ExternalId).HasColumnName("external_id");
        // The attachment pair: Incus's used_by (only observations) and the customer's ask (only requests).
        builder.Property(v => v.CurrentAttachedVmId).HasColumnName("current_attached_vm_id");
        builder.Property(v => v.CurrentAttachedElsewhere).HasColumnName("current_attached_elsewhere");
        builder.Property(v => v.DesiredAttachedVmId).HasColumnName("desired_attached_vm_id");
        builder.Property(v => v.RequestFailureReason).HasColumnName("request_failure_reason");
        builder.Property(v => v.RequestFailureDetail).HasColumnName("request_failure_detail").HasColumnType("text");
        builder.Property(v => v.RequestFailedAt).HasColumnName("request_failed_at");
        builder.Property(v => v.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(v => v.TenantId);
    }
}
