using System.Text.Json;
using Titan.Application.Queries.Admin.Backup.Contracts;
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
        var item = new BackupItemQueryResult("postgres", "titan-db", null, null, null, health, null, null, null, []);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new BackupsQueryResult(null, new[] { item }), Web));

        Assert.Equal(expected, json.RootElement.GetProperty("items")[0].GetProperty("health").GetString());
    }

    [Fact]
    public void TheFieldsTheConsoleReads_KeepTheirNames()
    {
        var at = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var item = new BackupItemQueryResult("vm", "web-1", Guid.NewGuid(), at, 123, BackupHealth.Stale, "BaseTooOld", at,
            Guid.NewGuid(), [new BackupArchiveQueryResult("20261025T120000Z", at, 123)]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new BackupsQueryResult(at, new[] { item }), Web));

        var root = json.RootElement;
        Assert.Equal(at, root.GetProperty("readAt").GetDateTimeOffset());
        var row = root.GetProperty("items")[0];
        Assert.Equal(
            new[] { "archives", "health", "kind", "lastWalAt", "name", "reason", "resourceId", "sizeBytes", "takenAt", "tenantId" },
            row.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        // What the restore dialog offers: one archive of the resource (titan-ops features/backup/design.md#restore-as-new-resource).
        Assert.Equal(
            new[] { "backupKey", "sizeBytes", "takenAt" },
            row.GetProperty("archives")[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    // The page's list of admin restores, beside the items and the drills.
    [Fact]
    public void TheFieldsOfARestore_KeepTheirNames()
    {
        var at = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var restore = new BackupRestoreQueryResult("vm", Guid.NewGuid(), "web-1-restored", Guid.NewGuid(), at, "failed", at, "disk full");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new AdminBackupsResult(at, [], [], [restore]), Web));

        Assert.Equal(
            new[] { "items", "readAt", "restoreDrills", "restores" },
            json.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { "askedAt", "kind", "name", "reason", "resourceId", "state", "takenAt", "tenantId" },
            json.RootElement.GetProperty("restores")[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }
}
