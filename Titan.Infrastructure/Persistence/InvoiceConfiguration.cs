using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Billing.Invoice;
using Titan.Domain.Aggregates.Billing.UsageRecord;

namespace Titan.Infrastructure.Persistence;

public class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasColumnName("id");
        builder.Property(i => i.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(i => i.PeriodYear).HasColumnName("period_year").IsRequired();
        builder.Property(i => i.PeriodMonth).HasColumnName("period_month").IsRequired();
        // Status is the double-pay guard (titan-ops architecture/money-and-wallet.md#double-pay-guard): the second
        // payer's `UPDATE ... AND status = @loaded` matches no row. The wallet's Version cannot catch it, since two
        // sequential debits both pass (InvoiceSchemaTests.Invoice_Status_IsConcurrencyToken).
        builder.Property(i => i.Status).HasColumnName("status").HasConversion<string>().IsRequired().IsConcurrencyToken();
        builder.Property(i => i.IssuedAt).HasColumnName("issued_at").IsRequired();
        builder.Property(i => i.DueAt).HasColumnName("due_at").IsRequired();
        builder.Property(i => i.PaidAt).HasColumnName("paid_at");
        builder.Property(i => i.PaidBy).HasColumnName("paid_by");
        // Whole VND (titan-ops architecture/money-and-wallet.md#money-is-long-vnd; InvoiceSchemaTests.Invoice_Total_Is_Bigint).
        builder.Property(i => i.Total).HasColumnName("total").HasColumnType("bigint").IsRequired();
        builder.Property(i => i.Currency).HasColumnName("currency").IsRequired();

        // One invoice per tenant per period, enforced by the database: two concurrent generation runs could both
        // win a check-then-insert (InvoiceSchemaTests.Invoice_HasUniqueIndex_On_Tenant_Period).
        builder.HasIndex(i => new { i.TenantId, i.PeriodYear, i.PeriodMonth }).IsUnique();

        builder.HasMany(i => i.Items)
            .WithOne()
            .HasForeignKey(item => item.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(i => i.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class InvoiceItemConfiguration : IEntityTypeConfiguration<InvoiceItem>
{
    public void Configure(EntityTypeBuilder<InvoiceItem> builder)
    {
        builder.ToTable("invoice_items");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasColumnName("id");
        builder.Property(i => i.InvoiceId).HasColumnName("invoice_id").IsRequired();
        // Same explicit token mapping as usage_records.resource_type: the value is a snapshot copied from a
        // UsageRecord, so it must persist the identical uppercase token (titan-ops architecture/code-structure.md#enum-storage).
        builder.Property(i => i.ResourceType).HasColumnName("resource_type")
            .HasConversion(v => v.ToDbString(), s => UsageResourceTypeExtensions.FromDbString(s))
            .IsRequired();
        builder.Property(i => i.ResourceId).HasColumnName("resource_id").IsRequired();
        builder.Property(i => i.ResourceName).HasColumnName("resource_name").HasMaxLength(64).IsRequired();
        builder.Property(i => i.Amount).HasColumnName("amount").HasColumnType("bigint").IsRequired();
        builder.Property(i => i.Currency).HasColumnName("currency").IsRequired();
    }
}
