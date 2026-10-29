using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Compute.LoadBalancer;

namespace Titan.Infrastructure.Persistence;

public class LoadBalancerConfiguration : IEntityTypeConfiguration<LoadBalancer>
{
    public void Configure(EntityTypeBuilder<LoadBalancer> builder)
    {
        builder.ToTable("load_balancers");
        builder.HasKey(lb => lb.Id);
        builder.Property(lb => lb.Id).HasColumnName("id");
        builder.Property(lb => lb.TenantId).HasColumnName("tenant_id");
        builder.Property(lb => lb.NetworkId).HasColumnName("network_id");
        builder.Property(lb => lb.ExternalNetworkId).HasColumnName("external_network_id").IsRequired();
        builder.Property(lb => lb.NetworkSubnet).HasColumnName("network_subnet").IsRequired();
        builder.Property(lb => lb.ClusterId).HasColumnName("cluster_id").IsRequired();
        builder.Property(lb => lb.Name).HasColumnName("name").IsRequired().HasMaxLength(100);
        builder.Property(lb => lb.VipIndex).HasColumnName("vip_index");
        builder.Property(lb => lb.Vip).HasColumnName("vip").IsRequired();
        builder.Property(lb => lb.ExternalId).HasColumnName("external_id");
        // Incus has no status word for it: existence only (titan-ops features/load-balancer/design.md#lb-lifecycle).
        builder.Property(lb => lb.CurrentExistence).HasColumnName("current_existence").HasConversion<string>().IsRequired();
        builder.Property(lb => lb.DesiredExistence).HasColumnName("desired_existence").HasConversion<string>().IsRequired();
        builder.Property(lb => lb.PendingIntentId).HasColumnName("pending_intent_id");
        builder.Ignore(lb => lb.Transition);
        builder.Property(lb => lb.RequestFailureReason).HasColumnName("request_failure_reason");
        builder.Property(lb => lb.RequestFailureDetail).HasColumnName("request_failure_detail").HasColumnType("text");
        builder.Property(lb => lb.RequestFailedAt).HasColumnName("request_failed_at");
        builder.Property(lb => lb.CreatedAt).HasColumnName("created_at");

        builder.Ignore(lb => lb.DesiredListeners);
        builder.Ignore(lb => lb.DesiredBackends);
        builder.Ignore(lb => lb.AppliedListeners);
        builder.Ignore(lb => lb.AppliedBackends);
        builder.HasMany(lb => lb.Listeners).WithOne().HasForeignKey(l => l.LoadBalancerId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(lb => lb.Listeners).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany(lb => lb.Backends).WithOne().HasForeignKey(b => b.LoadBalancerId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(lb => lb.Backends).UsePropertyAccessMode(PropertyAccessMode.Field);

        // The name stays taken until the delete command confirms it gone. Same filter as ExistsByNameAsync.
        builder.HasIndex(lb => new { lb.TenantId, lb.Name })
            .IsUnique()
            .HasFilter("current_existence <> 'Deleted'");
        // One VIP per live load balancer of a network: two creates that picked the same number race here
        // (titan-ops features/load-balancer/design.md#vip-in-network-64). Same condition as ListVipIndexesOfNetworkAsync.
        builder.HasIndex(lb => new { lb.NetworkId, lb.VipIndex })
            .IsUnique()
            .HasDatabaseName(VipIndexName)
            .HasFilter("current_existence <> 'Deleted'");
    }

    public const string VipIndexName = "ix_load_balancers_network_id_vip_index";
}

public class LoadBalancerListenerConfiguration : IEntityTypeConfiguration<LoadBalancerListener>
{
    public void Configure(EntityTypeBuilder<LoadBalancerListener> builder)
    {
        builder.ToTable("load_balancer_listeners");
        builder.HasKey(l => l.Id);
        // The aggregate sets the id. Left "generated on add", EF reads a row added to a tracked load balancer as one
        // already saved and updates it: 0 rows (LoadBalancerDbTests.AnUpdatesRows_RoundTrip_AndTheAppliedReportDropsTheOldOnes).
        builder.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(l => l.LoadBalancerId).HasColumnName("load_balancer_id").IsRequired();
        builder.Property(l => l.Protocol).HasColumnName("protocol").HasConversion<string>().IsRequired();
        builder.Property(l => l.Port).HasColumnName("port");
        builder.Property(l => l.TargetPort).HasColumnName("target_port");
        // No unique (protocol, port): while an update waits, the listener Incus has and the one asked for may share
        // them (titan-ops features/load-balancer/design.md#lb-resource-model). The aggregate keeps the asked ones distinct.
        builder.Property(l => l.IsDesired).HasColumnName("is_desired");
        builder.Property(l => l.IsApplied).HasColumnName("is_applied");
    }
}

public class LoadBalancerBackendConfiguration : IEntityTypeConfiguration<LoadBalancerBackend>
{
    public void Configure(EntityTypeBuilder<LoadBalancerBackend> builder)
    {
        builder.ToTable("load_balancer_backends");
        builder.HasKey(b => b.Id);
        // As the listener id above.
        builder.Property(b => b.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(b => b.LoadBalancerId).HasColumnName("load_balancer_id").IsRequired();
        builder.Property(b => b.VmId).HasColumnName("vm_id");
        builder.Property(b => b.ExternalVmId).HasColumnName("external_vm_id").IsRequired();
        builder.Property(b => b.IsDesired).HasColumnName("is_desired");
        builder.Property(b => b.IsApplied).HasColumnName("is_applied");
        // IsBackendAsync, on every VM delete.
        builder.HasIndex(b => b.VmId);
    }
}
