using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Vm;

namespace Titan.Infrastructure.Persistence;

public class VmMetricSampleConfiguration : IEntityTypeConfiguration<VmMetricSample>
{
    public void Configure(EntityTypeBuilder<VmMetricSample> builder)
    {
        builder.ToTable("vm_metric_samples");
        // One reading per VM per sample time: a batch the worker sends again meets the key and is skipped.
        builder.HasKey(s => new { s.VmId, s.SampledAt });
        builder.Property(s => s.VmId).HasColumnName("vm_id");
        builder.Property(s => s.SampledAt).HasColumnName("sampled_at");
        builder.Property(s => s.Source).HasColumnName("source").IsRequired();
        builder.Property(s => s.CpuSeconds).HasColumnName("cpu_seconds");
        builder.Property(s => s.MemoryTotalBytes).HasColumnName("memory_total_bytes");
        builder.Property(s => s.MemoryAvailableBytes).HasColumnName("memory_available_bytes");
        builder.Property(s => s.RootFsSizeBytes).HasColumnName("root_fs_size_bytes");
        builder.Property(s => s.RootFsAvailBytes).HasColumnName("root_fs_avail_bytes");
        builder.Property(s => s.DiskReadBytes).HasColumnName("disk_read_bytes");
        builder.Property(s => s.DiskWrittenBytes).HasColumnName("disk_written_bytes");

        // No navigation: Vm knows nothing of its samples. They are the VM's history, so they go with its row.
        builder.HasOne<Vm>()
            .WithMany()
            .HasForeignKey(s => s.VmId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
