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
    public const string LeasedNoTenantIndex = "IX_provider_commands_kind_leased_no_tenant";

    public void Configure(EntityTypeBuilder<ProviderCommand> builder)
    {
        builder.ToTable("provider_commands");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id");
        builder.Property(c => c.Key).HasColumnName("key").HasMaxLength(128).IsRequired();
        builder.Property(c => c.Kind).HasColumnName("kind").HasMaxLength(64).IsRequired();
        builder.Property(c => c.ClusterId).HasColumnName("cluster_id").HasMaxLength(64).IsRequired();
        builder.Property(c => c.TenantId).HasColumnName("tenant_id");
        // The side of the backup split (ProviderCommandKinds.Backup) a row is on: with the tenant, its lane
        // (titan-ops features/provider/design.md#tenant-lane).
        builder.Property<string>(LaneColumn).HasColumnName("lane").HasMaxLength(16)
            .HasComputedColumnSql(
                $"CASE WHEN kind IN ({string.Join(", ", ProviderCommandKinds.Backup.Select(k => $"'{k}'"))}) " +
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
        // commit (ProviderCommandRepository.ListClaimableAsync).
        builder.HasIndex(nameof(ProviderCommand.TenantId), LaneColumn).IsUnique()
            .HasFilter("status = 'Leased' AND tenant_id IS NOT NULL")
            .HasDatabaseName(LeasedPerTenantIndex);
        // The same for rows of no tenant, per kind: template.sync is the one such kind, so two syncs never hold a lease at once.
        builder.HasIndex(c => c.Kind).IsUnique()
            .HasFilter("status = 'Leased' AND tenant_id IS NULL")
            .HasDatabaseName(LeasedNoTenantIndex);
        // The lease scan: open rows of some kinds, oldest first.
        builder.HasIndex(c => new { c.Status, c.Kind, c.AvailableAt });
        // The retention pass: finished rows by age (ProviderCommand.IsFinishedAndCreatedBefore).
        builder.HasIndex(c => new { c.Status, c.CreatedAt });
    }
}
