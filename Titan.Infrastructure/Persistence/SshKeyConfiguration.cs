using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.SshKey;

namespace Titan.Infrastructure.Persistence;

public class SshKeyConfiguration : IEntityTypeConfiguration<SshKey>
{
    public void Configure(EntityTypeBuilder<SshKey> builder)
    {
        builder.ToTable("ssh_keys");
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Id).HasColumnName("id");
        builder.Property(k => k.TenantId).HasColumnName("tenant_id");
        builder.Property(k => k.Name).HasColumnName("name").HasMaxLength(SshKey.MaxNameLength).IsRequired();
        builder.Property(k => k.Type).HasColumnName("type").HasMaxLength(32).IsRequired();
        builder.Property(k => k.PublicKey).HasColumnName("public_key").HasColumnType("text").IsRequired();
        builder.Property(k => k.Fingerprint).HasColumnName("fingerprint").HasMaxLength(64).IsRequired();
        builder.Property(k => k.CreatedAt).HasColumnName("created_at");
        // A hard delete frees both at once, so neither index needs a filter. The handler checks first (409); these
        // are the net under two adds racing.
        builder.HasIndex(k => new { k.TenantId, k.Fingerprint }).IsUnique();
        builder.HasIndex(k => new { k.TenantId, k.Name }).IsUnique();
    }
}
