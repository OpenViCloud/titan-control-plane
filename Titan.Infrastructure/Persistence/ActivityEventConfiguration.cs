using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.ActivityEvent;

namespace Titan.Infrastructure.Persistence;

public class ActivityEventConfiguration : IEntityTypeConfiguration<ActivityEvent>
{
    public void Configure(EntityTypeBuilder<ActivityEvent> builder)
    {
        builder.ToTable("activity_events");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id");
        builder.Property(a => a.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(a => a.Action).HasColumnName("action").HasMaxLength(64).IsRequired();
        builder.Property(a => a.ResourceType).HasColumnName("resource_type").HasMaxLength(32).IsRequired();
        builder.Property(a => a.ResourceId).HasColumnName("resource_id").IsRequired();
        builder.Property(a => a.ResourceName).HasColumnName("resource_name").HasMaxLength(255).IsRequired();
        builder.Property(a => a.ActorKeycloakUserId).HasColumnName("actor_keycloak_user_id").HasMaxLength(255).IsRequired();
        builder.Property(a => a.CreatedAt).HasColumnName("created_at").IsRequired();

        // The feed query is "newest N events for this tenant": one index serves the filter and the ordering.
        builder.HasIndex(a => new { a.TenantId, a.CreatedAt })
            .HasDatabaseName("IX_activity_events_tenant_id_created_at")
            .IsDescending(false, true);
    }
}
