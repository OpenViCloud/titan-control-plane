using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.Invoice;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// Idempotent generation is enforced by a unique index on (TenantId, PeriodYear, PeriodMonth), not by
// application code checking-then-inserting. EF's InMemory provider enforces no index and
// property.GetColumnType() throws against it, so this pins the model metadata the migration is
// generated from — same approach as UsageRecordSchemaTests.
public class InvoiceSchemaTests
{
    private static TitanDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options);

    private static string? ConfiguredColumnType(IProperty property) =>
        property.FindAnnotation(RelationalAnnotationNames.ColumnType)?.Value as string;

    [Fact]
    public void Invoice_HasUniqueIndex_On_Tenant_Period()
    {
        using var db = CreateContext();

        var index = db.Model.FindEntityType(typeof(Invoice))!.GetIndexes()
            .SingleOrDefault(i =>
                i.IsUnique &&
                i.Properties.Select(p => p.Name).ToHashSet().SetEquals(
                [
                    nameof(Invoice.TenantId),
                    nameof(Invoice.PeriodYear),
                    nameof(Invoice.PeriodMonth)
                ]));

        Assert.NotNull(index);
    }

    // A tenant bills many periods over its life — the constraint must key on the period too, not
    // just the tenant, or a second month could never be billed.
    [Fact]
    public void Invoice_UniqueIndex_IsNotJustOnTenant()
    {
        using var db = CreateContext();

        var index = db.Model.FindEntityType(typeof(Invoice))!.GetIndexes()
            .Single(i =>
                i.IsUnique &&
                i.Properties.Select(p => p.Name).ToHashSet().SetEquals(
                [
                    nameof(Invoice.TenantId),
                    nameof(Invoice.PeriodYear),
                    nameof(Invoice.PeriodMonth)
                ]));

        Assert.Equal(3, index.Properties.Count);
    }

    // titan-ops architecture/money-and-wallet.md#money-is-long-vnd — money is whole VND (long → bigint).
    [Fact]
    public void Invoice_Total_Is_Bigint()
    {
        using var db = CreateContext();

        var property = db.Model.FindEntityType(typeof(Invoice))!.FindProperty(nameof(Invoice.Total))!;

        Assert.Equal("bigint", ConfiguredColumnType(property));
    }

    [Fact]
    public void InvoiceItem_Amount_Is_Bigint()
    {
        using var db = CreateContext();

        var property = db.Model.FindEntityType(typeof(InvoiceItem))!.FindProperty(nameof(InvoiceItem.Amount))!;

        Assert.Equal("bigint", ConfiguredColumnType(property));
    }

    // Deleting an invoice (not a supported app operation today, but a defensive schema invariant)
    // must take its items with it — an orphaned invoice_items row with no parent is meaningless.
    [Fact]
    public void InvoiceItem_ForeignKey_CascadesOnInvoiceDelete()
    {
        using var db = CreateContext();

        var fk = db.Model.FindEntityType(typeof(InvoiceItem))!
            .GetForeignKeys()
            .Single(f => f.Properties.Any(p => p.Name == nameof(InvoiceItem.InvoiceId)));

        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
    }

    // InvoiceItem.ResourceType is a C# enum copied frozen from a UsageRecord, and must persist as the
    // identical uppercase token via the same explicit converter. Dropping the converter on invoice_items
    // makes this null, and red.
    [Fact]
    public void InvoiceItem_ResourceType_IsStoredAsString()
    {
        using var db = CreateContext();

        var property = db.Model.FindEntityType(typeof(InvoiceItem))!
            .FindProperty(nameof(InvoiceItem.ResourceType))!;
        var converter = property.GetValueConverter();

        Assert.NotNull(converter);
        Assert.Equal(typeof(string), converter!.ProviderClrType);
    }

    // Invoice.Status is a C# enum but MUST persist as its member name ("New"/"Paid"/"SettledByWallet"),
    // as existing rows hold it. Storing the enum's integer would silently re-map every existing row and
    // change what the double-pay guard's `WHERE status = @loaded` matches; dropping HasConversion<string>()
    // turns this red. GetProviderClrType() fits the generic HasConversion<string>() form: GetValueConverter()
    // returns null for it on the InMemory model, as GetProviderClrType() does for a lambda converter.
    [Fact]
    public void Invoice_Status_IsStoredAsString()
    {
        using var db = CreateContext();

        var property = db.Model.FindEntityType(typeof(Invoice))!.FindProperty(nameof(Invoice.Status))!;

        Assert.Equal(typeof(string), property.GetProviderClrType());
    }

    // Status is the optimistic-concurrency token (titan-ops architecture/money-and-wallet.md#double-pay-guard):
    // EF's UPDATE must carry `WHERE ... AND status = @loaded` so two payers racing the same invoice can't
    // both settle it. Dropping IsConcurrencyToken() turns this red.
    [Fact]
    public void Invoice_Status_IsConcurrencyToken()
    {
        using var db = CreateContext();

        var property = db.Model.FindEntityType(typeof(Invoice))!.FindProperty(nameof(Invoice.Status))!;

        Assert.True(property.IsConcurrencyToken);
    }
}
