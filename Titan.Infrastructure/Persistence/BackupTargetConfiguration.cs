using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using BackupTargetAggregate = Titan.Domain.Aggregates.Platform.BackupTarget.BackupTarget;

namespace Titan.Infrastructure.Persistence;

public class BackupTargetConfiguration : IEntityTypeConfiguration<BackupTargetAggregate>
{
    public void Configure(EntityTypeBuilder<BackupTargetAggregate> builder)
    {
        builder.ToTable("backup_targets");
        // The id is the aggregate's fixed SingletonId: the primary key is what keeps it to one row.
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(t => t.Endpoint).HasColumnName("endpoint").HasMaxLength(2048).IsRequired();
        builder.Property(t => t.Region).HasColumnName("region").HasMaxLength(64).IsRequired();
        builder.Property(t => t.Bucket).HasColumnName("bucket").HasMaxLength(BackupTargetAggregate.MaxBucketLength).IsRequired();
        builder.Property(t => t.AccessKeyId).HasColumnName("access_key_id").HasMaxLength(128).IsRequired();
        builder.Property(t => t.SecretCipher).HasColumnName("secret_cipher").IsRequired();
        builder.Property(t => t.UpdatedAt).HasColumnName("updated_at").IsRequired();
    }
}
