using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.FunctionRuntime;

namespace Titan.Infrastructure.Persistence;

// The runtime catalog of OVC Function, global (no tenant_id), seeded here as the template catalog is
// (titan-ops features/ovc-function/design.md#runtimes).
public class FunctionRuntimeConfiguration : IEntityTypeConfiguration<FunctionRuntime>
{
    public void Configure(EntityTypeBuilder<FunctionRuntime> builder)
    {
        builder.ToTable("function_runtimes");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.Name).HasColumnName("name").HasColumnType("text").IsRequired();
        builder.Property(r => r.Image).HasColumnName("image").HasColumnType("text").IsRequired();
        builder.Property(r => r.Architecture).HasColumnName("architecture").HasColumnType("text").IsRequired();
        // No store default: EF would skip writing false (the CLR default) and the database would put true instead.
        builder.Property(r => r.IsListed).HasColumnName("is_listed").IsRequired();
        builder.Property(r => r.SupportEndsOn).HasColumnName("support_ends_on");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(r => r.Name).IsUnique().HasDatabaseName("UX_function_runtimes_name");

        builder.HasMany(r => r.Clusters)
            .WithOne()
            .HasForeignKey(c => c.RuntimeId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(r => r.Clusters).UsePropertyAccessMode(PropertyAccessMode.Field);

        SeedRuntimes(builder);
    }

    // HasData needs constant keys and values: Guid.NewGuid()/UtcNow would churn the migration on every build. The images
    // are what OpenViCloud/titan-function-runtimes published, each pinned by the digest of its one linux/amd64 manifest;
    // the end dates are the language's own (endoflife.date), go names none.
    private static void SeedRuntimes(EntityTypeBuilder<FunctionRuntime> builder)
    {
        var ts = new DateTimeOffset(2026, 11, 28, 0, 0, 0, TimeSpan.Zero);
        builder.HasData(
            Row("f0de0000-0000-4000-8000-000000000001", "python3.15",
                "ghcr.io/openvicloud/titan-function-runtimes/python3.15@sha256:5479dbe0d05ec82ba2ba0e6995a95619b231019639a7bf9aeb393a19d61c5a0b",
                new DateOnly(2031, 10, 31), ts),
            Row("f0de0000-0000-4000-8000-000000000002", "nodejs26",
                "ghcr.io/openvicloud/titan-function-runtimes/nodejs26@sha256:9f06d060020e71de5e31fa6a8bfcd6b85cca5491282df2b28abeec4b1e386c3c",
                new DateOnly(2029, 4, 30), ts),
            Row("f0de0000-0000-4000-8000-000000000003", "go",
                "ghcr.io/openvicloud/titan-function-runtimes/go@sha256:f253fc8f929cefbfdf899cdf8804374c0eb78e23d650f552840ea9e334d116dd",
                null, ts));
    }

    // FunctionRuntime's factory stamps NewGuid()/UtcNow, so HasData maps an anonymous row by property name. No cluster rows:
    // the first sync-all asks each runtime in every configured function cluster (SyncFunctionRuntimesCommandHandler).
    private static object Row(string id, string name, string image, DateOnly? supportEndsOn, DateTimeOffset ts) => new
    {
        Id = Guid.Parse(id),
        Name = name,
        Image = image,
        Architecture = "amd64",
        IsListed = true,
        SupportEndsOn = supportEndsOn,
        CreatedAt = ts,
    };
}

public class FunctionRuntimeClusterConfiguration : IEntityTypeConfiguration<FunctionRuntimeCluster>
{
    public void Configure(EntityTypeBuilder<FunctionRuntimeCluster> builder)
    {
        builder.ToTable("function_runtime_clusters");
        // The key IS the invariant "one sync state per runtime per function cluster", and keeps EF from reading a cluster
        // added to a tracked runtime as an existing row, as VmTemplateClusterConfiguration.
        builder.HasKey(c => new { c.RuntimeId, c.ClusterId });
        builder.Property(c => c.RuntimeId).HasColumnName("runtime_id");
        builder.Property(c => c.ClusterId).HasColumnName("cluster_id").HasColumnType("text");
        // The request columns of vm_template_clusters (no provider id), so core.pending_requests reads a waiting sync of
        // either the same way.
        builder.Property(c => c.Status).HasColumnName("status").HasConversion<string>().IsRequired();
        builder.OwnsOne(c => c.Operation, o =>
        {
            o.Property(x => x.PendingId).HasColumnName("pending_request_id");
            o.Property(x => x.AskedAt).HasColumnName("pending_request_asked_at");
            o.Property(x => x.FailureReason).HasColumnName("request_failure_reason").HasColumnType("text");
            o.Property(x => x.FailureDetail).HasColumnName("request_failure_detail").HasColumnType("text");
            o.Property(x => x.FailedAt).HasColumnName("request_failed_at");
            o.Property(x => x.LastSeenStatus).HasColumnName("observed_state").HasConversion<string>();
        });
        builder.Ignore(c => c.IsAvailable);
    }
}
