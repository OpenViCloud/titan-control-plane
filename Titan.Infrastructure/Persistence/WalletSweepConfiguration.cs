using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Titan.Infrastructure.Persistence;

public class WalletSweepConfiguration : IEntityTypeConfiguration<WalletSweep>
{
    public void Configure(EntityTypeBuilder<WalletSweep> builder)
    {
        builder.ToTable("wallet_sweeps");
        builder.HasKey(s => s.Job);
        builder.Property(s => s.Job).HasColumnName("job").HasMaxLength(50);
        builder.Property(s => s.RegisteredAt).HasColumnName("registered_at").IsRequired();
        builder.Property(s => s.LastAttemptAt).HasColumnName("last_attempt_at");
        builder.Property(s => s.LastSuccessAt).HasColumnName("last_success_at");
    }
}
