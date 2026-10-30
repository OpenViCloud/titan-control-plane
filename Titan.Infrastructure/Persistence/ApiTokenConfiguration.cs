using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.ApiToken;

namespace Titan.Infrastructure.Persistence;

public class ApiTokenConfiguration : IEntityTypeConfiguration<ApiToken>
{
    public void Configure(EntityTypeBuilder<ApiToken> builder)
    {
        builder.ToTable("api_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.TenantId).HasColumnName("tenant_id");
        builder.Property(t => t.KeycloakUserId).HasColumnName("keycloak_user_id").HasMaxLength(255).IsRequired();
        builder.Property(t => t.Name).HasColumnName("name").HasMaxLength(ApiToken.MaxNameLength).IsRequired();
        builder.Property(t => t.Prefix).HasColumnName("prefix").HasMaxLength(ApiToken.PrefixLength).IsRequired();
        builder.Property(t => t.SecretHash).HasColumnName("secret_hash").HasMaxLength(64).IsRequired();
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.Property(t => t.ExpiresAt).HasColumnName("expires_at");
        // Every request with a token looks it up by hash, across all tenants. A hard delete frees both indexes
        // at once, so neither needs a filter. The handler checks first (409); these are the net under a race.
        builder.HasIndex(t => t.SecretHash).IsUnique();
        builder.HasIndex(t => new { t.TenantId, t.Name }).IsUnique();
    }
}
