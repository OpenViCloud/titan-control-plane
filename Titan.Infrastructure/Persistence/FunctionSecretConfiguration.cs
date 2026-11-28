using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.FunctionSecret;

namespace Titan.Infrastructure.Persistence;

// A tenant's function secrets and their versions, ciphertext only (titan-ops features/ovc-function/design.md#function-secrets).
public class FunctionSecretConfiguration : IEntityTypeConfiguration<FunctionSecret>
{
    public void Configure(EntityTypeBuilder<FunctionSecret> builder)
    {
        builder.ToTable("function_secrets");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id");
        builder.Property(s => s.TenantId).HasColumnName("tenant_id");
        builder.Property(s => s.Name).HasColumnName("name").IsRequired().HasMaxLength(FunctionSecret.NameMaxLength);
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Ignore(s => s.NextVersionNumber);
        builder.Ignore(s => s.LatestVersionNumber);

        builder.HasMany(s => s.Versions).WithOne().HasForeignKey(v => v.SecretId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);

        // A hard delete frees the name at once, so no filter. The handler checks first (409); this is the net under two
        // creates racing, and the lookup by name of every secret route.
        builder.HasIndex(s => new { s.TenantId, s.Name }).IsUnique();
    }
}

public class FunctionSecretVersionConfiguration : IEntityTypeConfiguration<FunctionSecretVersion>
{
    public void Configure(EntityTypeBuilder<FunctionSecretVersion> builder)
    {
        builder.ToTable("function_secret_versions");
        // The key IS the invariant "one version per number per secret", and keeps EF from reading a version added to a
        // tracked secret as an existing row, as FunctionRuntimeClusterConfiguration.
        builder.HasKey(v => new { v.SecretId, v.Number });
        builder.Property(v => v.SecretId).HasColumnName("secret_id");
        builder.Property(v => v.Number).HasColumnName("number").ValueGeneratedNever();
        builder.Property(v => v.KeyId).HasColumnName("key_id").HasColumnType("text").IsRequired();
        builder.Property(v => v.Nonce).HasColumnName("nonce").IsRequired();
        builder.Property(v => v.Ciphertext).HasColumnName("ciphertext").IsRequired();
        builder.Property(v => v.ValueBytes).HasColumnName("value_bytes");
        builder.Property(v => v.CreatedAt).HasColumnName("created_at");
        builder.Property(v => v.CreatedBy).HasColumnName("created_by").HasColumnType("text").IsRequired();
    }
}
