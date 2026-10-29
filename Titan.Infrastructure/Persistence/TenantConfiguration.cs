using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Platform.Tenant;

namespace Titan.Infrastructure.Persistence;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.Name).HasColumnName("name").IsRequired();
        builder.Property(t => t.Email).HasColumnName("email").IsRequired();
        builder.Property(t => t.Status).HasColumnName("status").HasConversion<string>().IsRequired();
        // A value object, so ComplexProperty: columns in this table, no identity. OwnsOne would hand the model an
        // owned-entity key it does not need (titan-ops architecture/tenancy-and-admin.md#tenant-quota).
        // max_vms / max_networks keep their original names: renaming them would be a data migration, not an
        // additive one (TenantQuotaSchemaTests.PreExistingColumns_KeepTheirNames).
        builder.ComplexProperty(t => t.Quota, q =>
        {
            q.Property(x => x.MaxVms).HasColumnName("max_vms");
            q.Property(x => x.MaxNetworks).HasColumnName("max_networks");
            q.Property(x => x.MaxVolumes).HasColumnName("max_volumes");
            q.Property(x => x.MaxVmSnapshotsPerVm).HasColumnName("max_vm_snapshots_per_vm");
            q.Property(x => x.MaxVolumeSnapshotsPerVolume).HasColumnName("max_volume_snapshots_per_volume");
            q.Property(x => x.MaxFirewallRulesPerNetwork).HasColumnName("max_firewall_rules_per_network");
            q.Property(x => x.MaxPublicIpv6).HasColumnName("max_public_ipv6");
            q.Property(x => x.MaxSshKeys).HasColumnName("max_ssh_keys");
            q.Property(x => x.MaxLoadBalancers).HasColumnName("max_load_balancers");
        });
        // Whole VND like Invoice.Total, because the admin reads them side by side at month-end close.
        builder.Property(t => t.DebtCeiling).HasColumnName("debt_ceiling").HasColumnType("bigint").IsRequired();
        builder.Ignore(t => t.ProjectName);
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(t => t.Name).IsUnique();

        builder.HasMany(t => t.Members)
               .WithOne(m => m.Tenant)
               .HasForeignKey(m => m.TenantId);

        builder.Navigation(t => t.Members).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class MemberConfiguration : IEntityTypeConfiguration<Member>
{
    public void Configure(EntityTypeBuilder<Member> builder)
    {
        builder.ToTable("tenant_members");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("id");
        builder.Property(m => m.TenantId).HasColumnName("tenant_id");
        builder.Property(m => m.KeycloakUserId).HasColumnName("keycloak_user_id").IsRequired();
        builder.Property(m => m.JoinedAt).HasColumnName("joined_at");

        builder.HasIndex(m => new { m.TenantId, m.KeycloakUserId }).IsUnique();
        builder.HasIndex(m => m.KeycloakUserId).IsUnique();
    }
}
