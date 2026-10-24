using System.Text.Json;
using Titan.Application.Queries.Platform.Backup.Contracts;

namespace Titan.Tests.Services;

// The admin console reads health as a word. This API writes an enum as a string only where the property says so, so the word
// is pinned here instead of being left to the serializer's default (a number).
public class BackupsQueryResultJsonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(BackupHealth.Ok, "Ok")]
    [InlineData(BackupHealth.Stale, "Stale")]
    [InlineData(BackupHealth.Missing, "Missing")]
    [InlineData(BackupHealth.Unknown, "Unknown")]
    public void Health_IsWrittenAsItsName(BackupHealth health, string expected)
    {
        var item = new BackupItemQueryResult("postgres", "titan-db", null, null, null, health, null, null);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new BackupsQueryResult(null, new[] { item }), Web));

        Assert.Equal(expected, json.RootElement.GetProperty("items")[0].GetProperty("health").GetString());
    }

    [Fact]
    public void TheFieldsTheConsoleReads_KeepTheirNames()
    {
        var at = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var item = new BackupItemQueryResult("postgres", "titan-db", null, at, 123, BackupHealth.Stale, "BaseTooOld", at);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new BackupsQueryResult(at, new[] { item }), Web));

        var root = json.RootElement;
        Assert.Equal(at, root.GetProperty("readAt").GetDateTimeOffset());
        var row = root.GetProperty("items")[0];
        Assert.Equal(
            new[] { "health", "kind", "lastWalAt", "name", "reason", "sizeBytes", "takenAt", "tenantId" },
            row.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }
}
