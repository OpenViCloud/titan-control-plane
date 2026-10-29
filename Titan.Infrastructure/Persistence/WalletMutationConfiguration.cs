using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Billing.WalletMutation;

namespace Titan.Infrastructure.Persistence;

// The durable queue of the wallet outbox: the bus only notifies, and the sweeper re-drives rows it missed
// (titan-ops architecture/money-and-wallet.md#single-writer-outbox).
public class WalletMutationConfiguration : IEntityTypeConfiguration<WalletMutation>
{
    public void Configure(EntityTypeBuilder<WalletMutation> builder)
    {
        builder.ToTable("wallet_mutations");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id");
        builder.Property(c => c.WalletId).HasColumnName("wallet_id").IsRequired();
        // titan-ops architecture/code-structure.md#enum-storage (WalletMutationSchemaTests.Type_IsStoredAsString)
        builder.Property(c => c.Type)
            .HasColumnName("type").HasConversion<string>().IsRequired();
        // Positive whole VND (titan-ops architecture/money-and-wallet.md#money-is-long-vnd): Type carries the sign.
        builder.Property(c => c.Amount)
            .HasColumnName("amount").HasColumnType("bigint").IsRequired();
        builder.Property(c => c.RefType).HasColumnName("ref_type").HasMaxLength(32).IsRequired();
        builder.Property(c => c.RefId).HasColumnName("ref_id").IsRequired();
        // A concurrency token: reopening a failed debit is an UPDATE, and a press that read the row as Failed
        // must not write Pending over the Applied another press committed (WalletMutationSchemaTests).
        builder.Property(c => c.Status)
            .HasColumnName("status").HasConversion<string>().IsRequired().IsConcurrencyToken();
        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        // The database default only serves a writer that does not know the column (the previous image during a
        // rollout); this code always sets it.
        builder.Property(c => c.QueuedAt).HasColumnName("queued_at").IsRequired().HasDefaultValueSql("now()");
        builder.Property(c => c.AppliedAt).HasColumnName("applied_at");
        builder.Property(c => c.Attempts).HasColumnName("attempts").IsRequired();
        builder.Property(c => c.LastError).HasColumnName("last_error");

        // One source document has exactly one mutation row, whatever its status: two concurrent enqueues
        // collide here (23505), and paying an invoice again reopens its failed row
        // (titan-ops features/billing/invoice/design.md#retry-after-failed-debit). InMemory does not enforce it:
        // WalletMutationSchemaTests pins the metadata, WalletMutationIdempotencyDbTests and
        // InvoiceDebitRetryDbTests the behaviour.
        builder.HasIndex(c => new { c.RefType, c.RefId }).IsUnique();
        // The consumer/sweeper scan for work by wallet and by pending age.
        builder.HasIndex(c => new { c.WalletId, c.Status });
        builder.HasIndex(c => new { c.Status, c.QueuedAt });
    }
}
