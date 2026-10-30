using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Aggregates.Wallet;

namespace Titan.Infrastructure.Persistence;

// titan-ops architecture/money-and-wallet.md#wallet-one-balance
public class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> builder)
    {
        builder.ToTable("wallets");
        builder.HasKey(w => w.Id);
        builder.Property(w => w.Id).HasColumnName("id");
        builder.Property(w => w.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(w => w.Currency).HasColumnName("currency").IsRequired();
        // Whole VND, bigint (titan-ops architecture/money-and-wallet.md#money-is-long-vnd): same type as Invoice.Total,
        // so a balance and the invoice it pays compare exactly.
        builder.Property(w => w.AvailableAmount)
            .HasColumnName("available_amount").HasColumnType("bigint").IsRequired();
        // `AND version = @loaded` on every wallet UPDATE: a payer who read a stale balance loses at commit
        // instead of overdrawing (titan-ops architecture/money-and-wallet.md#version-token).
        builder.Property(w => w.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(w => w.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(w => w.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // One wallet per tenant, unfiltered: a wallet is never deleted or closed. This makes
        // OpenWalletCommandHandler safe under a concurrent open: the loser fails on the index and treats it as
        // success (WalletSchemaTests.WalletTenantId_IsUnique).
        builder.HasIndex(w => w.TenantId).IsUnique();
    }
}

public class WalletTransactionConfiguration : IEntityTypeConfiguration<WalletTransaction>
{
    public void Configure(EntityTypeBuilder<WalletTransaction> builder)
    {
        builder.ToTable("wallet_transactions");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.WalletId).HasColumnName("wallet_id").IsRequired();
        // Signed: positive in, negative out, so SUM(amount) per wallet equals wallets.available_amount
        // (titan-ops architecture/money-and-wallet.md#signed-ledger).
        builder.Property(t => t.Amount)
            .HasColumnName("amount").HasColumnType("bigint").IsRequired();
        // titan-ops architecture/code-structure.md#enum-storage (WalletSchemaTests.WalletTransactionType_IsStoredAsString)
        builder.Property(t => t.Type)
            .HasColumnName("type").HasConversion<string>().IsRequired();
        builder.Property(t => t.RefId).HasColumnName("ref_id").IsRequired();
        builder.Property(t => t.BalanceAfter)
            .HasColumnName("balance_after").HasColumnType("bigint").IsRequired();
        builder.Property(t => t.CreatedAt).HasColumnName("created_at").IsRequired();

        // The statement reads one wallet's movements in time order; reconciliation sums them per wallet.
        builder.HasIndex(t => new { t.WalletId, t.CreatedAt });

        // One movement per source document. The version token alone does not stop a double credit: a second
        // confirm that reads the wallet after the first commits passes it. This index fails that insert (23505)
        // and WalletRepository turns it into ConcurrencyConflictException (WalletSchemaTests.WalletTransactionTypeRefId_IsUnique).
        builder.HasIndex(t => new { t.Type, t.RefId }).IsUnique();
    }
}
