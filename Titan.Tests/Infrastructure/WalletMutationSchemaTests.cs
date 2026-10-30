using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// titan-ops architecture/money-and-wallet.md#single-writer-outbox — the InMemory provider enforces no unique
// index, so this pins the model metadata the migration is generated from. The refused second insert
// itself is proven on Postgres in WalletMutationIdempotencyDbTests.
public class WalletMutationSchemaTests
{
    private static IEntityType EntityType()
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);
        return db.Model.FindEntityType(typeof(WalletMutation))!;
    }

    private static IEnumerable<IIndex> Indexes() => EntityType().GetIndexes();

    // HasConversion<string>() reports through GetProviderClrType() only, a lambda HasConversion(v=>..,s=>..)
    // through GetValueConverter() only. Coalescing pins "stored as string" whichever idiom is used; a
    // removed converter leaves both null and fails.
    private static Type? StoredAsProviderType(IProperty property) =>
        property.GetValueConverter()?.ProviderClrType ?? property.GetProviderClrType();

    // One source document produces exactly one mutation, so two confirms of the same top-up cannot
    // enqueue two credits.
    [Fact]
    public void UniquePerRefTypeAndRefId()
    {
        var index = Indexes().SingleOrDefault(i =>
            i.IsUnique &&
            i.Properties.Select(p => p.Name).ToHashSet().SetEquals(
            [
                nameof(WalletMutation.RefType),
                nameof(WalletMutation.RefId)
            ]));

        Assert.NotNull(index);
        Assert.Equal(2, index!.Properties.Count);
    }

    // titan-ops features/billing/invoice/design.md#retry-after-failed-debit — one row per source document whatever
    // its status: paying an invoice again reopens its failed debit row instead of adding a second one.
    [Fact]
    public void UniquePerRefTypeAndRefId_HasNoFilter()
    {
        var index = Indexes().Single(i =>
            i.IsUnique &&
            i.Properties.Select(p => p.Name).ToHashSet().SetEquals(
            [
                nameof(WalletMutation.RefType),
                nameof(WalletMutation.RefId)
            ]));

        Assert.Null(index.GetFilter());
    }

    // Reopening a failed row is an UPDATE: a press that read the row as Failed must not write Pending over the
    // Applied that another press and the consumer committed meanwhile.
    [Fact]
    public void Status_IsAConcurrencyToken()
    {
        var property = EntityType().FindProperty(nameof(WalletMutation.Status))!;

        Assert.True(property.IsConcurrencyToken);
    }

    // The sweeper's freshness clock and the health check's pending age read queued_at, not created_at: a
    // reopened row keeps the created_at of its first press.
    [Fact]
    public void QueuedAt_IsARequiredColumn_IndexedWithStatus()
    {
        var property = EntityType().FindProperty("QueuedAt");

        Assert.NotNull(property);
        Assert.Equal("queued_at", property!.GetColumnName());
        Assert.False(property.IsNullable);
        Assert.Equal("now()", property.GetDefaultValueSql());
        Assert.Contains(Indexes(), i =>
            i.Properties.Select(p => p.Name).SequenceEqual(["Status", "QueuedAt"]));
    }

    // The key is the PAIR, not ref_id alone: two different source kinds can legitimately share a guid
    // space, so keying on ref_id only would wrongly collide a TopUp and an Invoice.
    [Fact]
    public void IdempotencyKeyIsNotJustRefId()
    {
        var refIdOnly = Indexes().Any(i =>
            i.IsUnique &&
            i.Properties.Count == 1 &&
            i.Properties[0].Name == nameof(WalletMutation.RefId));

        Assert.False(refIdOnly);
    }

    // Type persists as "Credit"/"Debit", not 0/1 — a hand-written money-path query reads a token, and
    // inserting a member mid-enum never re-maps existing rows.
    [Fact]
    public void Type_IsStoredAsString()
    {
        var property = EntityType().FindProperty(nameof(WalletMutation.Type))!;

        Assert.Equal(typeof(string), StoredAsProviderType(property));
    }

    [Fact]
    public void Status_IsStoredAsString()
    {
        var property = EntityType().FindProperty(nameof(WalletMutation.Status))!;

        Assert.Equal(typeof(string), StoredAsProviderType(property));
    }

    // titan-ops architecture/money-and-wallet.md#money-is-long-vnd — amount is whole VND (long -> bigint), never a wide decimal.
    [Fact]
    public void Amount_IsBigint()
    {
        var property = EntityType().FindProperty(nameof(WalletMutation.Amount))!;
        var configured = property
            .FindAnnotation(RelationalAnnotationNames.ColumnType)?.Value as string;

        Assert.Equal("bigint", configured);
    }

    // The sweeper's raw SQL (WalletMutationRepository.ListPendingOlderThanAsync) names this table as a
    // literal, so a rename that misses ToTable() passes every InMemory test and fails only on Postgres.
    [Fact]
    public void TableName_IsWalletMutations()
    {
        Assert.Equal("wallet_mutations", EntityType().GetTableName());
    }
}
