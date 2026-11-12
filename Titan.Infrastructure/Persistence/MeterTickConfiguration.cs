using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Titan.Infrastructure.Persistence;

public class MeterTickConfiguration : IEntityTypeConfiguration<MeterTick>
{
    public void Configure(EntityTypeBuilder<MeterTick> builder)
    {
        builder.ToTable("meter_ticks");
        builder.HasKey(t => t.Meter);
        builder.Property(t => t.Meter).HasColumnName("meter").HasMaxLength(50);
        builder.Property(t => t.RegisteredAt).HasColumnName("registered_at").IsRequired();
        builder.Property(t => t.LastAttemptAt).HasColumnName("last_attempt_at");
        builder.Property(t => t.LastSuccessAt).HasColumnName("last_success_at");
    }
}
