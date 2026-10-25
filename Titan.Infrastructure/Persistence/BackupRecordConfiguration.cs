using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Platform.BackupRecord;

namespace Titan.Infrastructure.Persistence;

public class BackupRecordConfiguration : IEntityTypeConfiguration<BackupRecord>
{
    public void Configure(EntityTypeBuilder<BackupRecord> builder)
    {
        builder.ToTable("backups");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(r => r.Kind).HasColumnName("kind").HasMaxLength(32).IsRequired();
        builder.Property(r => r.ResourceName).HasColumnName("resource_name").HasMaxLength(128).IsRequired();
        builder.Property(r => r.BackupKey).HasColumnName("backup_key").HasMaxLength(BackupRecord.MaxBackupKeyLength).IsRequired();
        builder.Property(r => r.TakenAt).HasColumnName("taken_at").IsRequired();
        builder.Property(r => r.SizeBytes).HasColumnName("size_bytes").IsRequired();
        builder.Property(r => r.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.ObservedAt).HasColumnName("observed_at").IsRequired();
        // One row per backup the bucket shows: the refresh upserts by this key.
        builder.HasIndex(r => new { r.Kind, r.ResourceName, r.BackupKey }).IsUnique();
    }
}
