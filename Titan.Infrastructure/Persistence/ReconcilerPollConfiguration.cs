using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Titan.Infrastructure.Persistence;

public class ReconcilerPollConfiguration : IEntityTypeConfiguration<ReconcilerPoll>
{
    public void Configure(EntityTypeBuilder<ReconcilerPoll> builder)
    {
        builder.ToTable("reconciler_polls");
        builder.HasKey(p => p.Key);
        builder.Property(p => p.Key).HasColumnName("key").HasMaxLength(200);
        builder.Property(p => p.PolledAt).HasColumnName("polled_at").IsRequired();
    }
}
