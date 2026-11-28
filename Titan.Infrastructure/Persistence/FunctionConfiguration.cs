using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Function;

namespace Titan.Infrastructure.Persistence;

// A customer's function and its versions (titan-ops features/ovc-function/design.md#function-lifecycle).
public class FunctionConfiguration : IEntityTypeConfiguration<Function>
{
    public void Configure(EntityTypeBuilder<Function> builder)
    {
        builder.ToTable("functions");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).HasColumnName("id");
        builder.Property(f => f.TenantId).HasColumnName("tenant_id");
        builder.Property(f => f.ClusterId).HasColumnName("cluster_id").HasColumnType("text").IsRequired();
        builder.Property(f => f.Name).HasColumnName("name").IsRequired().HasMaxLength(Function.NameMaxLength);
        builder.Property(f => f.ExternalId).HasColumnName("external_id").HasColumnType("text");
        // The request columns of the other provisioned tables, so core.pending_requests reads a waiting deploy or delete
        // the same way; observed_state is what the cluster was last seen holding while one waits.
        builder.Property(f => f.Status).HasColumnName("status").HasConversion<string>().IsRequired();
        builder.OwnsOne(f => f.Operation, o =>
        {
            o.Property(x => x.PendingId).HasColumnName("pending_request_id");
            o.Property(x => x.AskedAt).HasColumnName("pending_request_asked_at");
            o.Property(x => x.FailureReason).HasColumnName("request_failure_reason").HasColumnType("text");
            o.Property(x => x.FailureDetail).HasColumnName("request_failure_detail").HasColumnType("text");
            o.Property(x => x.FailedAt).HasColumnName("request_failed_at");
            o.Property(x => x.LastSeenStatus).HasColumnName("observed_state").HasConversion<string>();
        });
        builder.Property(f => f.RunningVersionId).HasColumnName("running_version_id");
        builder.Property(f => f.DeployingVersionId).HasColumnName("deploying_version_id");
        builder.Property(f => f.CreatedAt).HasColumnName("created_at");
        builder.Ignore(f => f.IsSeenOnTheCluster);
        builder.Ignore(f => f.IsReconcilable);
        builder.Ignore(f => f.VersionsToClean);

        builder.HasMany(f => f.Versions).WithOne().HasForeignKey(v => v.FunctionId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(f => f.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);

        // ListByTenantAsync and the quota count.
        builder.HasIndex(f => f.TenantId);
    }
}

public class FunctionVersionConfiguration : IEntityTypeConfiguration<FunctionVersion>
{
    public void Configure(EntityTypeBuilder<FunctionVersion> builder)
    {
        builder.ToTable("function_versions");
        builder.HasKey(v => v.Id);
        // The upload chooses the id before the row exists (it names the zip and the image tag). Left "generated on add",
        // EF reads a version added to a tracked function as one already saved and updates it: 0 rows.
        builder.Property(v => v.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(v => v.FunctionId).HasColumnName("function_id").IsRequired();
        builder.Property(v => v.Number).HasColumnName("number");
        builder.Property(v => v.RuntimeId).HasColumnName("runtime_id");
        builder.Property(v => v.RuntimeName).HasColumnName("runtime_name").HasColumnType("text").IsRequired();
        builder.Property(v => v.RuntimeImage).HasColumnName("runtime_image").HasColumnType("text").IsRequired();
        builder.Property(v => v.Handler).HasColumnName("handler").HasColumnType("text");
        builder.Property(v => v.MemoryMiB).HasColumnName("memory_mib");
        builder.Property(v => v.TimeoutSeconds).HasColumnName("timeout_seconds");
        builder.Property(v => v.CodeKey).HasColumnName("code_key").HasColumnType("text").IsRequired();
        builder.Property(v => v.CodeSha256).HasColumnName("code_sha256").HasColumnType("text").IsRequired();
        builder.Property(v => v.CodeBytes).HasColumnName("code_bytes");
        builder.Property(v => v.UnpackedBytes).HasColumnName("unpacked_bytes");
        builder.Property(v => v.FileCount).HasColumnName("file_count");
        builder.Property(v => v.ImageDigest).HasColumnName("image_digest").HasColumnType("text").IsRequired();
        builder.Property(v => v.Image).HasColumnName("image").HasColumnType("text").IsRequired();
        builder.Property(v => v.CreatedAt).HasColumnName("created_at");
        builder.Property(v => v.DiscardedAt).HasColumnName("discarded_at");
        builder.Ignore(v => v.CpuMillicores);
        builder.Ignore(v => v.IsDiscarded);

        // One number per function: two uploads take turns on the function's row, and this is the second net.
        builder.HasIndex(v => new { v.FunctionId, v.Number }).IsUnique();
    }
}
