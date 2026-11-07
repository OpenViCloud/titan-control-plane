using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Network;

namespace Titan.Infrastructure.Persistence;

public class NetworkConfiguration : IEntityTypeConfiguration<Network>
{
    public void Configure(EntityTypeBuilder<Network> builder)
    {
        builder.ToTable("networks");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).HasColumnName("id");
        builder.Property(n => n.TenantId).HasColumnName("tenant_id");
        builder.Property(n => n.Name).HasColumnName("name").IsRequired();
        builder.Property(n => n.ClusterId).HasColumnName("cluster_id").IsRequired();
        builder.Property(n => n.ChosenCidr).HasColumnName("requested_cidr");
        builder.Property(n => n.Cidr).HasColumnName("cidr");

        builder.Property(n => n.ExternalId).HasColumnName("external_id");
        builder.Property(n => n.Gateway).HasColumnName("gateway");
        // titan-ops architecture/desired-and-current.md#desired-and-current: the Incus word (null while Incus has none)
        // and the existence pair, two writers.
        builder.Property(n => n.CurrentState).HasColumnName("current_state").HasConversion<string>();
        builder.Property(n => n.CurrentExistence).HasColumnName("current_existence").HasConversion<string>().IsRequired();
        builder.Property(n => n.DesiredExistence).HasColumnName("desired_existence").HasConversion<string>().IsRequired();
        // The waiting operation (id, kind, when asked) and the last failure, six columns the aggregate writes through one
        // NetworkOperation (titan-ops features/network/design.md#network-operation). OwnsOne, not ComplexProperty: the
        // InMemory provider of EF Core 9.0.4 cannot load an entity that has a complex property (titan-ops
        // features/vm/design.md#vm-data-model).
        builder.OwnsOne(n => n.Operation, o =>
        {
            o.Property(x => x.PendingId).HasColumnName("pending_request_id");
            o.Property(x => x.Kind).HasColumnName("pending_request_kind").HasConversion<string>();
            o.Property(x => x.AskedAt).HasColumnName("pending_request_asked_at");
            o.Property(x => x.FailureReason).HasColumnName("request_failure_reason");
            o.Property(x => x.FailureDetail).HasColumnName("request_failure_detail").HasColumnType("text");
            o.Property(x => x.FailedAt).HasColumnName("request_failed_at");
        });
        builder.Ignore(n => n.Transition);
        builder.Property(n => n.CreatedAt).HasColumnName("created_at");

        builder.Property(n => n.DefaultIngressPolicy)
            .HasColumnName("default_ingress_policy")
            .HasConversion<string>()
            .IsRequired()
            .HasDefaultValue(FirewallPolicy.Allow);
        builder.Property(n => n.DefaultEgressPolicy)
            .HasColumnName("default_egress_policy")
            .HasConversion<string>()
            .IsRequired()
            .HasDefaultValue(FirewallPolicy.Allow);
        builder.Property(n => n.PublicIpv6Subnet).HasColumnName("public_ipv6_subnet");
        builder.Ignore(n => n.HasPublicIpv6);

        // The rules are the network's (titan-ops architecture/code-structure.md#child-entity): loaded and saved with it,
        // through the backing field so EF never needs a setter on the collection. Restrict, not cascade: no row of
        // either table is ever deleted (titan-ops features/network/design.md#firewall-rule-soft-delete).
        builder.HasMany(n => n.FirewallRules)
            .WithOne()
            .HasForeignKey(r => r.NetworkId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(n => n.FirewallRules).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(n => n.FirewallRuleQuotaUsage);

        builder.HasIndex(n => new { n.TenantId, n.Name })
            .IsUnique()
            // The name stays taken until the delete command confirms the network gone: a delete in flight still
            // holds it (titan-ops features/network/design.md#network-name-reuse). Same filter as ExistsByNameAsync.
            .HasFilter("current_existence <> 'Deleted'");
    }
}
