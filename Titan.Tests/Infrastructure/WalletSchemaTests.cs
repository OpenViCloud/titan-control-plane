using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.Billing.TopUp;
using Titan.Domain.Aggregates.Billing.Wallet;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// The unique indexes on the money path are not enforced by the EF InMemory provider, so the model
// metadata the migration is generated from is what gets pinned here. top_ups.payment_ref UNIQUE: one
// TopUp per reconciliation code.
public class WalletSchemaTests
{
    private static IEnumerable<IIndex> IndexesOf<T>() where T : class
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);
        return db.Model.FindEntityType(typeof(T))!.GetIndexes().ToList();
    }

    [Fact]
    public void PaymentRef_IsUnique()
    {
        var index = IndexesOf<TopUp>().Single(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(TopUp.PaymentRef));

        Assert.True(index.IsUnique);
    }

    // Asserted on the model rather than by grepping the source: a grep proves a string is absent from
    // the files it scanned, which is a different claim from "EF maps no such table".
    [Fact]
    public void Model_HasNoReceiptEntity()
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);

        var mapped = db.Model.GetEntityTypes().Select(e => e.GetTableName()).ToList();

        Assert.DoesNotContain("receipts", mapped);
        Assert.DoesNotContain("wallet_entries", mapped);
        Assert.Contains("top_ups", mapped);          // non-empty oracle: the model really did load
        Assert.Contains("wallets", mapped);
    }

    // ── titan-ops architecture/money-and-wallet.md#wallet-one-balance: the single-balance wallet ────────────────────────────────────

    // One wallet per tenant. A check-then-insert in C# is a race, so this index is what stops a tenant
    // from ending up with two balances.
    [Fact]
    public void WalletTenantId_IsUnique()
    {
        var index = IndexesOf<Wallet>().Single(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(Wallet.TenantId));

        Assert.True(index.IsUnique);
    }

    // One ledger movement per source document (a mutation's apply, or a promo grant): a second row for the same
    // (type, ref_id) fails here. This pins the declaration only; no executed test reaches the index
    // (titan-ops features/billing/wallet/design.md#wallet-debts).
    [Fact]
    public void WalletTransactionTypeRefId_IsUnique()
    {
        var index = IndexesOf<WalletTransaction>().Single(i =>
            i.Properties.Count == 2
            && i.Properties[0].Name == nameof(WalletTransaction.Type)
            && i.Properties[1].Name == nameof(WalletTransaction.RefId));

        Assert.True(index.IsUnique);
    }

    // Stored as a readable string: an integer would make every hand-written money-path query guess what
    // "2" meant, and would silently re-map every row if a member were inserted mid-enum.
    [Fact]
    public void WalletTransactionType_IsStoredAsString()
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);

        var property = db.Model.FindEntityType(typeof(WalletTransaction))!
            .FindProperty(nameof(WalletTransaction.Type))!;

        Assert.Equal(typeof(string), property.GetProviderClrType());
    }

    // Persisted as the member name: an integer would re-map every existing "Confirmed"/"SePay" row. A
    // property with no converter returns null here, so this is red until the conversion is configured.
    [Fact]
    public void TopUpStatus_IsStoredAsString()
    {
        Assert.Equal(typeof(string), ProviderTypeOf<TopUp>(nameof(TopUp.Status)));
    }

    [Fact]
    public void TopUpSource_IsStoredAsString()
    {
        Assert.Equal(typeof(string), ProviderTypeOf<TopUp>(nameof(TopUp.Source)));
    }

    private static Type? ProviderTypeOf<T>(string propertyName) where T : class
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);
        return db.Model.FindEntityType(typeof(T))!.FindProperty(propertyName)!.GetProviderClrType();
    }
}
