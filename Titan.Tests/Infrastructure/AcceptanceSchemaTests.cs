using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.Acceptance;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// The unique index (subject_id, document_key, document_version) carries business
// meaning: one acceptance per subject per version (append-only — a new version is a new row), and it is
// the DB guard that makes RecordAcceptance idempotent under a double-click race. The InMemory provider
// does not enforce indexes, so the model metadata the migration is generated from is what gets pinned
// here (same shape/reason as WalletSchemaTests).
public class AcceptanceSchemaTests
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
    public void SubjectKeyVersion_IsUnique()
    {
        var index = IndexesOf<Acceptance>().Single(i =>
            i.Properties.Count == 3
            && i.Properties.Any(p => p.Name == nameof(Acceptance.SubjectId))
            && i.Properties.Any(p => p.Name == nameof(Acceptance.DocumentKey))
            && i.Properties.Any(p => p.Name == nameof(Acceptance.DocumentVersion)));

        Assert.True(index.IsUnique);
    }
}
