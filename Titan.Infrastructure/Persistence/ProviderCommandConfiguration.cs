using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.ProviderCommand;

namespace Titan.Infrastructure.Persistence;

// The durable queue between the control plane and the provider worker
// (titan-ops architecture/provider-and-worker.md#provider-command-table).
public class ProviderCommandConfiguration : IEntityTypeConfiguration<ProviderCommand>
{
    public const string LaneColumn = "Lane";
    public const string LeasedPerTenantIndex = "IX_provider_commands_tenant_id_lane_leased";

    public void Configure(EntityTypeBuilder<ProviderCommand> builder)
    {
        builder.ToTable("provider_commands");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id");
        builder.Property(c => c.Key).HasColumnName("key").HasMaxLength(128).IsRequired();
        builder.Property(c => c.Kind).HasColumnName("kind").HasMaxLength(64).IsRequired();
        builder.Property(c => c.ClusterId).HasColumnName("cluster_id").HasMaxLength(64).IsRequired();
        builder.Property(c => c.TenantId).HasColumnName("tenant_id");
        // The loop a row runs in: the worker leases the backup kinds on a loop of their own, so a tenant's backup and its
        // other commands are two lanes (titan-ops features/provider/user-stories.md#p3-2).
        builder.Property<string>(LaneColumn).HasColumnName("lane").HasMaxLength(16)
            .HasComputedColumnSql(
                $"CASE WHEN kind IN ('{ProviderCommandKinds.VmBackup}', '{ProviderCommandKinds.VolumeBackup}') " +
                "THEN 'backup' ELSE 'command' END", stored: true);
        builder.Property(c => c.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        // titan-ops architecture/code-structure.md#enum-storage
        builder.Property(c => c.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(c => c.Attempts).HasColumnName("attempts").IsRequired();
        builder.Property(c => c.AvailableAt).HasColumnName("available_at").IsRequired();
        builder.Property(c => c.LeaseId).HasColumnName("lease_id");
        builder.Property(c => c.LeaseUntil).HasColumnName("lease_until");
        builder.Property(c => c.LastError).HasColumnName("last_error");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        // Two leases of the same row: the second UPDATE ... AND version = N matches 0 rows.
        builder.Property(c => c.Version).HasColumnName("version").IsConcurrencyToken();

        // One key, one row: two concurrent first enqueues collide here (23505). InMemory does not enforce it:
        // ProviderCommandSchemaTests pins the metadata.
        builder.HasIndex(c => c.Key).IsUnique();
        builder.HasIndex(c => c.LeaseId).IsUnique();
        // One leased row per tenant and lane: two leases that read at once and pick two rows of one tenant cannot both
        // commit (ProviderCommandRepository.ListClaimableAsync). Rows of no tenant are kept apart by the query alone.
        builder.HasIndex(nameof(ProviderCommand.TenantId), LaneColumn).IsUnique()
            .HasFilter("status = 'Leased' AND tenant_id IS NOT NULL")
            .HasDatabaseName(LeasedPerTenantIndex);
        // The lease scan: open rows of some kinds, oldest first.
        builder.HasIndex(c => new { c.Status, c.Kind, c.AvailableAt });
        // The retention pass: finished rows by age (ProviderCommand.IsFinishedAndCreatedBefore).
        builder.HasIndex(c => new { c.Status, c.CreatedAt });
    }
}
