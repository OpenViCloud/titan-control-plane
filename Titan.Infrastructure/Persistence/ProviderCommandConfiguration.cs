using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Platform.ProviderCommand;

namespace Titan.Infrastructure.Persistence;

// The durable queue between the control plane and the provider worker
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/provider-and-worker.md#provider-command-table).
public class ProviderCommandConfiguration : IEntityTypeConfiguration<ProviderCommand>
{
    public void Configure(EntityTypeBuilder<ProviderCommand> builder)
    {
        builder.ToTable("provider_commands");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id");
        builder.Property(c => c.Key).HasColumnName("key").HasMaxLength(128).IsRequired();
        builder.Property(c => c.Kind).HasColumnName("kind").HasMaxLength(64).IsRequired();
        builder.Property(c => c.ClusterId).HasColumnName("cluster_id").HasMaxLength(64).IsRequired();
        builder.Property(c => c.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
        // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#enum-storage
        builder.Property(c => c.Rerun).HasColumnName("rerun").HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(c => c.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(c => c.Attempts).HasColumnName("attempts").IsRequired();
        builder.Property(c => c.AvailableAt).HasColumnName("available_at").IsRequired();
        builder.Property(c => c.LeaseId).HasColumnName("lease_id");
        builder.Property(c => c.LeaseUntil).HasColumnName("lease_until");
        builder.Property(c => c.LastError).HasColumnName("last_error");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(c => c.FinishedAt).HasColumnName("finished_at");
        // Two leases of the same row: the second UPDATE ... AND version = N matches 0 rows.
        builder.Property(c => c.Version).HasColumnName("version").IsConcurrencyToken();

        // One key, one row: two concurrent first enqueues collide here (23505). InMemory does not enforce it:
        // ProviderCommandSchemaTests pins the metadata.
        builder.HasIndex(c => c.Key).IsUnique();
        builder.HasIndex(c => c.LeaseId).IsUnique();
        // The lease scan: open rows of some kinds, oldest first.
        builder.HasIndex(c => new { c.Status, c.Kind, c.AvailableAt });
    }
}
