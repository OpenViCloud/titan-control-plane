using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Titan.Infrastructure.Persistence;

public class ConsoleTicketConfiguration : IEntityTypeConfiguration<ConsoleTicket>
{
    public void Configure(EntityTypeBuilder<ConsoleTicket> builder)
    {
        builder.ToTable("console_tickets");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsRequired();
        builder.Property(t => t.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(t => t.VmId).HasColumnName("vm_id").IsRequired();
        builder.Property(t => t.CreatedBy).HasColumnName("created_by").IsRequired();
        builder.Property(t => t.ExpiresAt).HasColumnName("expires_at").IsRequired();

        builder.HasIndex(t => t.TokenHash).IsUnique(); // the one row a ticket takes
        builder.HasIndex(t => t.TenantId); // a tenant's expired tickets, deleted at each issue
    }
}
