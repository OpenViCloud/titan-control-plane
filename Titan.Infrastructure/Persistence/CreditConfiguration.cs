using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.TopUp;

namespace Titan.Infrastructure.Persistence;

// A promo grant is a TopUp with Source=PromotionalCredit, so it lives in `top_ups`, not its own table.

public class TopUpConfiguration : IEntityTypeConfiguration<TopUp>
{
    public void Configure(EntityTypeBuilder<TopUp> builder)
    {
        builder.ToTable("top_ups");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(t => t.Amount).HasColumnName("amount").HasColumnType("bigint").IsRequired();
        builder.Property(t => t.Currency).HasColumnName("currency").IsRequired();
        // titan-ops architecture/code-structure.md#enum-storage (WalletSchemaTests.TopUpStatus_IsStoredAsString)
        builder.Property(t => t.Status).HasColumnName("status").HasConversion<string>().IsRequired();
        builder.Property(t => t.PaymentRef).HasColumnName("payment_ref").IsRequired();
        builder.Property(t => t.Source).HasColumnName("source").HasConversion<string>();
        builder.Property(t => t.PaymentTxnRef).HasColumnName("payment_txn_ref");
        builder.Property(t => t.FailureReason).HasColumnName("failure_reason");
        // Audit reason, set only on a PromotionalCredit TopUp.
        builder.Property(t => t.Reason).HasColumnName("reason").HasMaxLength(500);
        builder.Property(t => t.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(t => t.ConfirmedAt).HasColumnName("confirmed_at");
        builder.Property(t => t.ConfirmedBy).HasColumnName("confirmed_by");

        // Titan's reconciliation code, one TopUp per code; always set at Create, so no filter
        // (WalletSchemaTests.PaymentRef_IsUnique).
        builder.HasIndex(t => t.PaymentRef).IsUnique();
        builder.HasIndex(t => t.TenantId); // list a tenant's top-ups
    }
}
