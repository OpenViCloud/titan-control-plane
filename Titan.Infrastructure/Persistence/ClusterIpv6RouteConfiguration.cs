using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.ClusterIpv6Route;

namespace Titan.Infrastructure.Persistence;

public class ClusterIpv6RouteConfiguration : IEntityTypeConfiguration<ClusterIpv6Route>
{
    public void Configure(EntityTypeBuilder<ClusterIpv6Route> builder)
    {
        builder.ToTable("cluster_ipv6_routes");
        builder.HasKey(r => new { r.ClusterId, r.Number });
        builder.Property(r => r.ClusterId).HasColumnName("cluster_id");
        builder.Property(r => r.Number).HasColumnName("number").ValueGeneratedNever();
        builder.Property(r => r.NetworkId).HasColumnName("network_id");
        builder.Property(r => r.ReleasedAt).HasColumnName("released_at");
        builder.Ignore(r => r.IsFree);

        // A network holds at most one route; the network table is not referenced, the route keeps the id only.
        builder.HasIndex(r => r.NetworkId)
            .IsUnique()
            .HasFilter("network_id IS NOT NULL");
    }
}
