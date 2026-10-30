using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.PaymentGatewayTransaction;
using Titan.Domain.Aggregates.TopUp;

namespace Titan.Infrastructure.Persistence;

public class PaymentGatewayTransactionConfiguration : IEntityTypeConfiguration<PaymentGatewayTransaction>
{
    public void Configure(EntityTypeBuilder<PaymentGatewayTransaction> builder)
    {
        builder.ToTable("payment_transactions");

        // The SePay transaction id is the key: the DB is the durable dedupe store for webhook retries.
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");

        builder.Property(t => t.TopUpId).HasColumnName("top_up_id");
        builder.Property(t => t.TenantId).HasColumnName("tenant_id");
        builder.Property(t => t.Amount).HasColumnName("amount").HasColumnType("bigint").IsRequired();
        builder.Property(t => t.Gateway).HasColumnName("gateway").IsRequired();
        builder.Property(t => t.RawContent).HasColumnName("raw_content");
        // titan-ops architecture/code-structure.md#enum-storage
        builder.Property(t => t.Status).HasColumnName("status").HasConversion<string>().IsRequired();
        builder.Property(t => t.ReceivedAt).HasColumnName("received_at").IsRequired();
        builder.Property(t => t.TransactionDate).HasColumnName("transaction_date").IsRequired();

        // Reconciliation reads by status (find orphans/mismatches) and by the matched TopUp.
        builder.HasIndex(t => t.Status);
        builder.HasIndex(t => t.TopUpId);
    }
}
