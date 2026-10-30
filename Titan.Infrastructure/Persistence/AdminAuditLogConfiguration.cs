using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.AdminAuditLog;

namespace Titan.Infrastructure.Persistence;

public class AdminAuditLogConfiguration : IEntityTypeConfiguration<AdminAuditLog>
{
    public void Configure(EntityTypeBuilder<AdminAuditLog> builder)
    {
        builder.ToTable("admin_audit_log");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id");
        builder.Property(a => a.Action).HasColumnName("action").HasMaxLength(64).IsRequired();
        builder.Property(a => a.ActorKeycloakUserId).HasColumnName("actor_keycloak_user_id").HasMaxLength(255).IsRequired();
        builder.Property(a => a.ResourceType).HasColumnName("resource_type").HasMaxLength(32).IsRequired();
        builder.Property(a => a.ResourceId).HasColumnName("resource_id").IsRequired();
        builder.Property(a => a.OwnerTenantId).HasColumnName("owner_tenant_id").IsRequired();
        builder.Property(a => a.CreatedAt).HasColumnName("created_at").IsRequired();
        // Nullable on purpose: GUID-keyed, tenant-owned rows have no resource key, so a backfill would invent data.
        builder.Property(a => a.ResourceKey).HasColumnName("resource_key").HasMaxLength(64);
        builder.Property(a => a.Detail).HasColumnName("detail").HasMaxLength(512);

        // Query axes for "who deleted what for whom, when" (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-write).
        builder.HasIndex(a => a.CreatedAt);
        builder.HasIndex(a => a.OwnerTenantId);
    }
}
