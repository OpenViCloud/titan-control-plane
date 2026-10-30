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
        builder.Property(n => n.Cidr).HasColumnName("cidr");

        builder.Property(n => n.ExternalId).HasColumnName("external_id");
        builder.Property(n => n.Gateway).HasColumnName("gateway");
        // titan-ops architecture/desired-and-current.md#desired-and-current: the Incus word (null while Incus has none)
        // and the existence pair, two writers.
        builder.Property(n => n.CurrentState).HasColumnName("current_state").HasConversion<string>();
        builder.Property(n => n.CurrentExistence).HasColumnName("current_existence").HasConversion<string>().IsRequired();
        builder.Property(n => n.DesiredExistence).HasColumnName("desired_existence").HasConversion<string>().IsRequired();
        builder.Property(n => n.RequestFailureReason).HasColumnName("request_failure_reason");
        builder.Property(n => n.RequestFailureDetail).HasColumnName("request_failure_detail").HasColumnType("text");
        builder.Property(n => n.RequestFailedAt).HasColumnName("request_failed_at");
        builder.Property(n => n.PendingIntentId).HasColumnName("pending_intent_id");
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
        builder.Property(n => n.PublicIpv6Index).HasColumnName("public_ipv6_index");
        builder.Property(n => n.PublicIpv6Subnet).HasColumnName("public_ipv6_subnet");
        builder.Property(n => n.PublicIpv6RouterAddress).HasColumnName("public_ipv6_router_address");
        builder.Property(n => n.PublicIpv6ReleasedAt).HasColumnName("public_ipv6_released_at");

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

        // One /64 per live network of a cluster: two creates that picked the same number race here, not in Incus
        // (titan-ops infra/incus/design.md#ipv6-address-plan). Same condition as ListPublicIpv6IndexesInClusterAsync.
        builder.HasIndex(n => new { n.ClusterId, n.PublicIpv6Index })
            .IsUnique()
            .HasDatabaseName(PublicIpv6IndexName)
            .HasFilter("public_ipv6_index IS NOT NULL AND current_existence <> 'Deleted'");
    }

    public const string PublicIpv6IndexName = "ix_networks_cluster_id_public_ipv6_index";
}
