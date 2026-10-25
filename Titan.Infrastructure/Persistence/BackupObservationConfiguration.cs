using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Platform.BackupObservation;

namespace Titan.Infrastructure.Persistence;

public class BackupObservationConfiguration : IEntityTypeConfiguration<BackupObservation>
{
    public void Configure(EntityTypeBuilder<BackupObservation> builder)
    {
        builder.ToTable("backup_observations");
        builder.HasKey(o => new { o.Kind, o.ResourceName });
        builder.Property(o => o.Kind).HasColumnName("kind").HasMaxLength(32);
        builder.Property(o => o.ResourceName).HasColumnName("resource_name").HasMaxLength(128);
        builder.Property(o => o.Outcome).HasColumnName("outcome").HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(o => o.LastWalAt).HasColumnName("last_wal_at");
        builder.Property(o => o.LastReadAt).HasColumnName("last_read_at").IsRequired();
        builder.Property(o => o.Error).HasColumnName("error").HasMaxLength(BackupObservation.MaxErrorLength);
    }
}
