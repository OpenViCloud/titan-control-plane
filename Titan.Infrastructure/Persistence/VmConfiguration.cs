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
        // titan-ops architecture/desired-and-current.md#desired-and-current: what the worker reports writes current_*, a
        // request writes desired_* (Vm.cs names the exceptions). current_state holds the Incus status word for word (the enum names ARE the Incus
        // words), NULL while Incus has no instance.
        builder.Property(v => v.CurrentState).HasColumnName("current_state").HasConversion<string>();
        builder.Property(v => v.DesiredState).HasColumnName("desired_state").HasConversion<string>().IsRequired();
        builder.Property(v => v.CurrentExistence).HasColumnName("current_existence").HasConversion<string>().IsRequired();
        builder.Property(v => v.DesiredExistence).HasColumnName("desired_existence").HasConversion<string>().IsRequired();
        builder.Property(v => v.RequestFailureReason).HasColumnName("request_failure_reason");
        builder.Property(v => v.RequestFailureDetail).HasColumnName("request_failure_detail").HasColumnType("text");
        builder.Property(v => v.RequestFailedAt).HasColumnName("request_failed_at");
        builder.Property(v => v.ExternalId).HasColumnName("external_id");
        // has_been_running and create_finished stay in the table with DEFAULT false until rollout phase 6
        // (titan-ops architecture/resource-lifecycle.md#rollout); the model no longer reads them.
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
        // The one waiting request, as three columns the aggregate writes together (titan-ops architecture/resource-lifecycle.md#shape);
        // Vm.Pending is built from them on read. Private properties, mapped by name.
        builder.Property<Guid?>("PendingRequestId").HasColumnName("pending_request_id");
        builder.Property<VmRequestKind?>("PendingRequestKind").HasColumnName("pending_request_kind").HasConversion<string>();
        builder.Property<DateTimeOffset?>("PendingRequestAskedAt").HasColumnName("pending_request_asked_at");
        builder.Ignore(v => v.Pending);
        builder.Ignore(v => v.Transition);

        // Same condition as VmRepository.ExistsByNameAsync: the name is free again only once the VM is Deleted.
        builder.HasIndex(v => new { v.TenantId, v.Name })
            .IsUnique()
            .HasFilter("current_existence <> 'Deleted'");

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
