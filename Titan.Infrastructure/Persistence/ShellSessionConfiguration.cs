using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Titan.Infrastructure.Persistence;

public class ShellSessionConfiguration : IEntityTypeConfiguration<ShellSession>
{
    public void Configure(EntityTypeBuilder<ShellSession> builder)
    {
        builder.ToTable("shell_sessions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id");
        builder.Property(s => s.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(s => s.VmId).HasColumnName("vm_id").IsRequired();
        builder.Property(s => s.OpenedAt).HasColumnName("opened_at").IsRequired();
        builder.Property(s => s.SeenAt).HasColumnName("seen_at").IsRequired();

        builder.HasIndex(s => s.TenantId); // the count of one tenant's sessions
    }
}
