using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.ClusterIpv6Pool;

namespace Titan.Infrastructure.Persistence;

// titan-ops features/network/design.md#ipv6-public
public class ClusterIpv6PoolConfiguration : IEntityTypeConfiguration<ClusterIpv6Pool>
{
    public const string ClusterIndexName = "ix_cluster_ipv6_pools_cluster_id";

    public void Configure(EntityTypeBuilder<ClusterIpv6Pool> builder)
    {
        builder.ToTable("cluster_ipv6_pools");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(p => p.ClusterId).HasColumnName("cluster_id").IsRequired();
        // `AND version = @loaded` on every pool UPDATE: two requests that loaded the pool at once meet here, and the
        // later commit rolls its holding rows back with it. Every GiveBlock64To and TakeBlock64BackFrom moves the
        // version, and the pool has one kind of writer, unlike a root a reconciler also writes.
        builder.Property(p => p.Version).HasColumnName("version").IsConcurrencyToken();

        // The holdings are the pool's: loaded and saved with it, through the backing field. Restrict, not cascade: no
        // row of either table is ever deleted.
        builder.HasMany(p => p.Holdings)
            .WithOne()
            .HasForeignKey(h => h.PoolId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(p => p.Holdings).UsePropertyAccessMode(PropertyAccessMode.Field);

        // One pool per cluster. The first block given in a cluster creates the row, so two first gives at once meet
        // here; ErrorStatus answers them like the version clash.
        builder.HasIndex(p => p.ClusterId).IsUnique().HasDatabaseName(ClusterIndexName);
    }
}

public class Ipv6Block64HoldingConfiguration : IEntityTypeConfiguration<Ipv6Block64Holding>
{
    public const string HoldingKeyName = "pk_cluster_ipv6_block64_holdings";
    public const string HolderIndexName = "ix_cluster_ipv6_block64_holdings_holder_network_id";

    public void Configure(EntityTypeBuilder<Ipv6Block64Holding> builder)
    {
        builder.ToTable("cluster_ipv6_block64_holdings");
        builder.HasKey(h => new { h.PoolId, h.Number }).HasName(HoldingKeyName);
        builder.Property(h => h.PoolId).HasColumnName("pool_id");
        builder.Property(h => h.Number).HasColumnName("block64_number").ValueGeneratedNever();
        builder.Property(h => h.HolderNetworkId).HasColumnName("holder_network_id");
        builder.Property(h => h.GivenBackAt).HasColumnName("given_back_at");
        builder.Ignore(h => h.IsHeld);

        // A network holds at most one block; the network table is not referenced, the pool keeps ids only.
        builder.HasIndex(h => h.HolderNetworkId)
            .IsUnique()
            .HasFilter("holder_network_id IS NOT NULL")
            .HasDatabaseName(HolderIndexName);
    }
}
