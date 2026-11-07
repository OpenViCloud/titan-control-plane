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
        // titan-ops features/vm/design.md#vm-status: status is the one status. The operation is a value object kept in
        // this table's columns, never null: the waiting operation by id and the time it was asked, the last failure, and
        // observed_state, the Incus status seen while an operation waits (the enum names ARE the Incus statuses), NULL for a
        // row written while the VM is at rest. EF builds it even when every column is NULL (VmOperationDbTests). OwnsOne, not
        // ComplexProperty: EF Core 9.0.4's InMemory provider cannot load an entity that has a complex property (a
        // KeyNotFoundException at query compile, whatever the shape), and the repository state tests run on it.
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
        builder.Ignore(v => v.HasAnInstance);
        builder.Ignore(v => v.InstanceStatus);

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
