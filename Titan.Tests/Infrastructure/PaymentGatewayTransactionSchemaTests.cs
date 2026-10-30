using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.PaymentGatewayTransaction;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// PaymentGatewayTransaction.Status is stored as its member name, not the enum's default integer:
// reconciliation reads by status and the status index keys off the stored value. The InMemory provider
// has no store type, so the value-converter metadata is pinned; dropping the converter turns this red.
public class PaymentGatewayTransactionSchemaTests
{
    [Fact]
    public void Status_IsStoredAsString()
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);

        var property = db.Model.FindEntityType(typeof(PaymentGatewayTransaction))!
            .FindProperty(nameof(PaymentGatewayTransaction.Status))!;

        Assert.Equal(typeof(string), property.GetProviderClrType());
    }
}
