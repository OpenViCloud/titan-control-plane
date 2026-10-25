namespace Titan.Application.Commands.Platform.Backup;

// Where barman (Barman Cloud Plugin) puts Postgres backups in the bucket
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#backup-status-reads-the-bucket); measured on the dev bucket: base/<id>/backup.info and data.tar, and wals/.
public static class BackupLayout
{
    public const string PostgresResourceName = "titan-db";
    public const string PostgresPrefix = "postgres/titan-db/";
    public const string BasePrefix = PostgresPrefix + "base/";
    public const string WalPrefix = PostgresPrefix + "wals/";
    public const string BackupInfoFile = "backup.info";
}
