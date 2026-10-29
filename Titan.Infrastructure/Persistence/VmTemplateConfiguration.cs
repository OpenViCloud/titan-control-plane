using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Catalog.VmOffering;
using Titan.Domain.Aggregates.Catalog.VmTemplate;

namespace Titan.Infrastructure.Persistence;

public class VmTemplateConfiguration : IEntityTypeConfiguration<VmTemplate>
{
    public void Configure(EntityTypeBuilder<VmTemplate> builder)
    {
        builder.ToTable("vm_templates");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.Name).HasColumnName("name").HasColumnType("text").IsRequired();
        builder.Property(t => t.Description).HasColumnName("description").HasColumnType("text");
        builder.Property(t => t.OsImageName).HasColumnName("os_image_name").HasColumnType("text").IsRequired();
        builder.Property(t => t.RootDiskSizeGb).HasColumnName("root_disk_size_gb");
        builder.Property(t => t.ImageUrl).HasColumnName("image_url").HasColumnType("text");
        // No store default: EF would skip writing false (the CLR default) and the database would put true instead.
        builder.Property(t => t.IsListed).HasColumnName("is_listed").IsRequired();
        builder.Property(t => t.VmOfferingId).HasColumnName("vm_offering_id").HasColumnType("text").IsRequired();
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.Property(t => t.UpdatedAt).HasColumnName("updated_at");

        // No soft-delete filter, unlike Network: templates are never soft-deleted.
        builder.HasIndex(t => t.Name).IsUnique().HasDatabaseName("UX_vm_templates_name");

        // titan-ops features/catalog/design.md#template-borrows-sizing. Restrict, not Cascade: deleting a tier that
        // templates still point at must fail loudly, not quietly take rows of the OS catalog with it.
        builder.HasOne<VmOffering>()
            .WithMany()
            .HasForeignKey(t => t.VmOfferingId)
            .HasConstraintName("FK_vm_templates_vm_offerings_vm_offering_id")
            .OnDelete(DeleteBehavior.Restrict);

        // titan-ops features/catalog/design.md#template-per-cluster. The sync state of each cluster is a child row.
        builder.HasMany(t => t.Clusters)
            .WithOne()
            .HasForeignKey(z => z.TemplateId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(t => t.Clusters).UsePropertyAccessMode(PropertyAccessMode.Field);

        SeedTemplates(builder);
    }

    // titan-ops features/catalog/design.md#template-seed-in-migration. HasData needs constant keys and values:
    // Guid.NewGuid()/UtcNow would churn the migration on every build.
    private static void SeedTemplates(EntityTypeBuilder<VmTemplate> builder)
    {
        var ts = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

        // ImageUrl is the distro's own image the worker imports; without it the worker pulls OsImageName from the image
        // server (titan-ops features/catalog/design.md#template-is-an-image-alias). Not listed ⇒ customers neither see nor
        // pick it (titan-ops features/catalog/design.md#template-listed). Every row points at a tier Titan sells. Deleted seed rows leave their template on the provider:
        // template sync is add-only (titan-ops features/catalog/design.md#template-sync-add-only).
        builder.HasData(
            Row("0a5e0000-0000-4000-8000-000000000002", "Ubuntu-22.04-Standard", "Ubuntu 22.04 LTS general purpose", "ubuntu/22.04",
                UbuntuJammyImage, true, 25, "m", ts),
            Row("0a5e0000-0000-4000-8000-000000000003", "Alma-9-DB", "AlmaLinux 9 for database servers", "almalinux/9",
                null, false, 40, "l", ts),
            Row("0a5e0000-0000-4000-8000-000000000005", "Rocky-9-App", "Rocky Linux 9 application server", "rockylinux/9",
                null, false, 30, "l", ts));
    }

    // VmTemplate's factory stamps NewGuid()/UtcNow, so HasData maps an anonymous row by property name. No cluster rows:
    // the first sync run asks for each seeded template in every configured cluster (SyncTemplatesCommandHandler).
    private const string UbuntuJammyImage =
        "https://cloud-images.ubuntu.com/releases/jammy/release/ubuntu-22.04-server-cloudimg-{arch}.img";

    private static object Row(string id, string name, string description, string osImageName, string? imageUrl,
        bool isListed, int rootDiskSizeGb, string vmOfferingId, DateTimeOffset ts) => new
        {
            Id = Guid.Parse(id),
            Name = name,
            Description = (string?)description,
            OsImageName = osImageName,
            ImageUrl = imageUrl,
            IsListed = isListed,
            RootDiskSizeGb = (int?)rootDiskSizeGb,
            VmOfferingId = vmOfferingId,
            CreatedAt = ts,
            UpdatedAt = ts,
        };
}

public class VmTemplateClusterConfiguration : IEntityTypeConfiguration<VmTemplateCluster>
{
    public void Configure(EntityTypeBuilder<VmTemplateCluster> builder)
    {
        builder.ToTable("vm_template_clusters");
        // The key IS the invariant "one sync state per template per cluster". A composite key also keeps EF from
        // reading a cluster added to a tracked template as an existing row (a generated Guid key that is already
        // set looks saved).
        builder.HasKey(z => new { z.TemplateId, z.ClusterId });
        builder.Property(z => z.TemplateId).HasColumnName("template_id");
        builder.Property(z => z.ClusterId).HasColumnName("cluster_id").HasColumnType("text");
        // Incus has no status word for an image alias: existence only (titan-ops features/catalog/design.md#template-state-is-existence).
        builder.Property(z => z.CurrentExistence).HasColumnName("current_existence").HasConversion<string>().IsRequired();
        builder.Property(z => z.DesiredExistence).HasColumnName("desired_existence").HasConversion<string>().IsRequired();
        builder.Property(z => z.PendingIntentId).HasColumnName("pending_intent_id");
        builder.Property(z => z.RequestFailureReason).HasColumnName("request_failure_reason").HasColumnType("text");
        builder.Property(z => z.RequestFailureDetail).HasColumnName("request_failure_detail").HasColumnType("text");
        builder.Property(z => z.RequestFailedAt).HasColumnName("request_failed_at");
        builder.Property(z => z.ExternalTemplateId).HasColumnName("external_template_id").HasColumnType("text");
        builder.Property(z => z.UpdatedAt).HasColumnName("updated_at");
        builder.Ignore(z => z.Transition);
    }
}
