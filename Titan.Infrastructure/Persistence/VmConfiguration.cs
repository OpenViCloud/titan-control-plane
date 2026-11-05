using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.VmOffering;
using Titan.Domain.Aggregates.Vm;

namespace Titan.Infrastructure.Persistence;

public class VmConfiguration : IEntityTypeConfiguration<Vm>
{
    public void Configure(EntityTypeBuilder<Vm> builder)
    {
        builder.ToTable("vms");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).HasColumnName("id");
        builder.Property(v => v.TenantId).HasColumnName("tenant_id");
        builder.Property(v => v.NetworkId).HasColumnName("network_id");
        builder.Property(v => v.Name).HasColumnName("name").IsRequired();
        // titan-ops features/vm/design.md#vm-status: status is the one status; observed_state is the Incus word the worker
        // read last, word for word (the enum names ARE the Incus words), NULL while none is known.
        builder.Property(v => v.Status).HasColumnName("status").HasConversion<string>().IsRequired();
        builder.Property(v => v.ObservedState).HasColumnName("observed_state").HasConversion<string>();
        builder.Property(v => v.OperationFailureReason).HasColumnName("request_failure_reason");
        builder.Property(v => v.OperationFailureDetail).HasColumnName("request_failure_detail").HasColumnType("text");
        builder.Property(v => v.OperationFailedAt).HasColumnName("request_failed_at");
        builder.Property(v => v.ExternalId).HasColumnName("external_id");
        builder.Property(v => v.IpAddress).HasColumnName("ip_address");
        builder.Property(v => v.VmOfferingId).HasColumnName("vm_offering_id").IsRequired();
        // titan-ops features/catalog/design.md#resources-reference-their-offering. No navigation: Compute holds no Catalog type.
        builder.HasOne<VmOffering>()
            .WithMany()
            .HasForeignKey(v => v.VmOfferingId)
            .HasConstraintName("FK_vms_vm_offerings_vm_offering_id")
            .OnDelete(DeleteBehavior.Restrict);
        builder.Property(v => v.TemplateId).HasColumnName("template_id").IsRequired();
        builder.Property(v => v.ClusterId).HasColumnName("cluster_id").IsRequired();
        builder.Property(v => v.DiskSizeGb).HasColumnName("disk_size_gb");
        builder.Property(v => v.UserData).HasColumnName("user_data").HasColumnType("text");
        builder.Property(v => v.PublicIpv6Address).HasColumnName("public_ipv6_address");
        builder.Property(v => v.CreatedAt).HasColumnName("created_at");
        // The one waiting request, by id and the time it was asked; the status says what it asks for
        // (titan-ops features/vm/design.md#vm-status).
        builder.Property(v => v.PendingOperationId).HasColumnName("pending_request_id");
        builder.Property(v => v.PendingOperationAskedAt).HasColumnName("pending_request_asked_at");
        builder.Ignore(v => v.HasAnInstance);

        // Same condition as VmRepository.ExistsByNameAsync: the name is free again only once the VM is Deleted.
        builder.HasIndex(v => new { v.TenantId, v.Name })
            .IsUnique()
            .HasFilter("status <> 'Deleted'");

        // The snapshots are the VM's (titan-ops architecture/code-structure.md#child-entity): loaded and saved with it,
        // through the backing field. Restrict, not cascade: no row of either table is ever deleted.
        builder.HasMany(v => v.Snapshots)
            .WithOne()
            .HasForeignKey(s => s.VmId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(v => v.Snapshots).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(v => v.SnapshotQuotaUsage);
    }
}
