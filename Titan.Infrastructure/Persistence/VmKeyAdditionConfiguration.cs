using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Vm;

namespace Titan.Infrastructure.Persistence;

public class VmKeyAdditionConfiguration : IEntityTypeConfiguration<VmKeyAddition>
{
    public void Configure(EntityTypeBuilder<VmKeyAddition> builder)
    {
        builder.ToTable("vm_key_additions");
        builder.HasKey(a => a.Id);
        // The aggregate sets the id, as VmSnapshotConfiguration (same trap otherwise: EF reads an addition added to a
        // tracked VM as one already saved and updates it, 0 rows).
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(a => a.VmId).HasColumnName("vm_id");
        builder.Property(a => a.TenantId).HasColumnName("tenant_id");
        // No FK to ssh_keys: a key is hard-deleted (titan-ops features/vm/design.md#ssh-key), and the ids picked here
        // are the record of what was asked for, not a live reference. Mapped through the backing field, typed as the
        // list it is: the read-only public property (IReadOnlyList<Guid>) is not itself a mappable column.
        builder.Ignore(a => a.SshKeyIds);
        builder.Property<List<Guid>>("_sshKeyIds").HasColumnName("ssh_key_ids");
        builder.Property(a => a.Status).HasColumnName("status").HasConversion<string>().IsRequired();
        builder.Property(a => a.PendingId).HasColumnName("pending_request_id");
        builder.Property(a => a.AskedAt).HasColumnName("pending_request_asked_at");
        builder.Property(a => a.FailureReason).HasColumnName("request_failure_reason");
        builder.Property(a => a.FailureDetail).HasColumnName("request_failure_detail").HasColumnType("text");
        builder.Property(a => a.FailedAt).HasColumnName("request_failed_at");
        builder.Property(a => a.RestartNeeded).HasColumnName("restart_needed");
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");
        builder.Ignore(a => a.IsTerminal);

        builder.HasIndex(a => a.VmId);
        builder.HasIndex(a => a.TenantId);
    }
}
