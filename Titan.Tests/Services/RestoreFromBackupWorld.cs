using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Admin.Backup;
using Titan.Application.Commands.Admin.Backup.Contracts;
using Titan.Domain.Aggregates.AdminAuditLog;
using Titan.Domain.Aggregates.BackupRecord;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Services;

/// A tenant's VMs and volumes with their backup archives, and the two admin restore handlers run over them
/// (titan-ops features/backup/design.md#restore-as-new-resource). Everything is the real implementation on one
/// InMemory database, each handler call on a context of its own as a request has: what a call staged and what its one
/// save wrote are read from that context, so "nothing was written" and "the row, its audit entry and its command
/// commit together" are measured, not assumed from a stub. InMemory takes no row lock and enforces no index: the lock
/// is pinned by RequestLifecycleShapeTests, the lane by ProviderCommandLeaseDbTests.
internal sealed class RestoreFromBackupWorld
{
    public const string Admin = "admin-1";
    public const string Cluster = "cluster-a";

    private readonly string _database = $"restore-from-backup-{Guid.NewGuid()}";
    private readonly SaveLog _saves = new();
    private readonly TickingClock _clock = new(new DateTimeOffset(2026, 11, 24, 3, 0, 0, TimeSpan.Zero));

    public Tenant Owner { get; } = TestTenant.WithRoomToSpare();

    /// The admin belongs to a tenant too: nothing a restore writes may land under it.
    public Guid AdminsOwnTenant { get; } = Guid.NewGuid();

    /// When the VM archives of this world are taken, unless a test says otherwise.
    public DateTimeOffset VmArchiveTakenAt { get; } = new(2026, 11, 20, 1, 0, 0, TimeSpan.Zero);

    public List<string> Warnings { get; } = [];

    /// One entry per SaveChanges of a handler call: the rows it wrote, as "State Type".
    public IReadOnlyList<IReadOnlyList<string>> Saves => _saves.Passes;

    /// What the last handler call left staged and unsaved in its context.
    public IReadOnlyList<string> LeftStaged { get; private set; } = [];

    /// The code a refusal answers with, whichever exception carries it.
    public static string? CodeOf(Exception refusal)
    {
        if (refusal is BusinessException business)
        {
            return business.Code;
        }
        if (refusal is NotFoundException notFound)
        {
            return notFound.Code;
        }
        if (refusal is ConflictException conflict)
        {
            return conflict.Code;
        }
        return null;
    }

    public static string KeyOf(DateTimeOffset takenAt) => takenAt.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>().UseInMemoryDatabase(_database).Options);

    // ---- What the tenant has.

    public async Task<T> HasAsync<T>(T row) where T : class
    {
        await using TitanDbContext db = NewDb();
        db.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    public Task<Vm> ARunningVmAsync(string name = "web-1", Tenant? owner = null)
    {
        Vm vm = Vm.Create((owner ?? Owner).Id, Guid.NewGuid(), name, "m", "tpl-1", Cluster, 40, userData: "#cloud-config\n",
            stopTimeoutSeconds: 600);
        vm.MarkRunning("vm-" + vm.Id.ToString("N")[..8], "10.0.0.5");
        return HasAsync(vm);
    }

    public static Volume AnAvailableVolume(Tenant owner, string name = "data-1", int sizeGb = 50)
    {
        Volume volume = Volume.Create(owner.Id, name, Cluster, "std", sizeGb);
        volume.MarkCreated("vol-" + volume.Id.ToString("N")[..8]);
        return volume;
    }

    public Task<Volume> AnAvailableVolumeAsync(string name = "data-1") => HasAsync(AnAvailableVolume(Owner, name));

    /// A volume Incus holds attached to the VM.
    public Task<Volume> AVolumeAttachedToAsync(Vm vm, string name)
    {
        Volume volume = AnAvailableVolume(Owner, name);
        volume.Attach(vm);
        volume.MarkAttached(vm.Id);
        return HasAsync(volume);
    }

    public Task<BackupRecord> AnArchiveAsync(string kind, Guid resourceId, DateTimeOffset takenAt,
        BackupRecordStatus status = BackupRecordStatus.Complete) =>
        HasAsync(BackupRecord.Create(kind, resourceId.ToString(), KeyOf(takenAt), takenAt, 2048, status, Owner.Id));

    public Task<BackupRecord> AVmArchiveAsync(Vm vm) => AnArchiveAsync(BackupRecord.VmKind, vm.Id, VmArchiveTakenAt);

    public Task<BackupRecord> AVolumeArchiveAsync(Volume volume, TimeSpan fromTheVms) =>
        AnArchiveAsync(BackupRecord.VolumeKind, volume.Id, VmArchiveTakenAt + fromTheVms);

    // ---- The two handlers, each call a request of its own.

    public RestoreVmFromBackupCommandHandler VmHandler(TitanDbContext db) =>
        new(new VmRepository(db), new VolumeRepository(db), new BackupRecordRepository(db), new AdminAuditLogRepository(db),
            new ProviderCommandQueue(new ProviderCommandRepository(db), _clock), new TheAdmin(AdminsOwnTenant),
            new WarningLog<RestoreVmFromBackupCommandHandler>(Warnings));

    public RestoreVolumeFromBackupCommandHandler VolumeHandler(TitanDbContext db) =>
        new(new VolumeRepository(db), new BackupRecordRepository(db), new AdminAuditLogRepository(db),
            new ProviderCommandQueue(new ProviderCommandRepository(db), _clock), new TheAdmin(AdminsOwnTenant),
            new WarningLog<RestoreVolumeFromBackupCommandHandler>(Warnings));

    public TitanDbContext NewRequestDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>().UseInMemoryDatabase(_database).AddInterceptors(_saves).Options);

    public async Task<Guid> RestoreVmAsync(Guid vmId, string backupKey, string name)
    {
        await using TitanDbContext db = NewRequestDb();
        try
        {
            return (await VmHandler(db).HandleAsync(new RestoreVmFromBackupCommand(vmId, backupKey, name))).NewVmId;
        }
        finally
        {
            LeftStaged = Unsaved(db);
        }
    }

    public async Task<Guid> RestoreVolumeAsync(Guid volumeId, string backupKey, string name)
    {
        await using TitanDbContext db = NewRequestDb();
        try
        {
            return (await VolumeHandler(db).HandleAsync(new RestoreVolumeFromBackupCommand(volumeId, backupKey, name))).NewVolumeId;
        }
        finally
        {
            LeftStaged = Unsaved(db);
        }
    }

    private static List<string> Unsaved(TitanDbContext db) =>
        db.ChangeTracker.Entries()
            .Where(e => e.State != EntityState.Unchanged && !e.Metadata.IsOwned())
            .Select(e => $"{e.State} {e.Metadata.ClrType.Name}")
            .Order(StringComparer.Ordinal)
            .ToList();

    // ---- What the database holds afterwards.

    public async Task<List<Vm>> VmsAsync()
    {
        await using TitanDbContext db = NewDb();
        return await db.Vms.AsNoTracking().ToListAsync();
    }

    public async Task<List<Volume>> VolumesAsync()
    {
        await using TitanDbContext db = NewDb();
        return await db.Volumes.AsNoTracking().ToListAsync();
    }

    /// In the order the worker's lease reads them: oldest first.
    public async Task<List<ProviderCommand>> CommandsAsync()
    {
        await using TitanDbContext db = NewDb();
        return (await db.ProviderCommands.AsNoTracking().ToListAsync()).OrderBy(c => c.CreatedAt).ToList();
    }

    public async Task<List<AdminAuditLog>> AuditAsync()
    {
        await using TitanDbContext db = NewDb();
        return await db.AdminAuditLogs.AsNoTracking().ToListAsync();
    }

    /// The rows of every table money or a tenant's feed is read from.
    public async Task<int> MoneyAndFeedRowsAsync()
    {
        await using TitanDbContext db = NewDb();
        return await db.Wallets.CountAsync() + await db.WalletTransactions.CountAsync() + await db.WalletMutations.CountAsync()
               + await db.UsageRecords.CountAsync() + await db.Invoices.CountAsync() + await db.TopUps.CountAsync()
               + await db.ActivityEvents.CountAsync();
    }

    public async Task<int> VmsCountedTowardQuotaAsync()
    {
        await using TitanDbContext db = NewDb();
        return await new VmRepository(db).CountActiveByTenantAsync(Owner.Id);
    }

    public async Task<int> VolumesCountedTowardQuotaAsync()
    {
        await using TitanDbContext db = NewDb();
        return await new VolumeRepository(db).CountActiveByTenantAsync(Owner.Id);
    }

    private sealed record TheAdmin(Guid OwnTenant) : ICurrentUser
    {
        public string KeycloakUserId => Admin;
        public Guid? TenantId => OwnTenant;
        public bool IsAdmin => true;
    }

    private sealed class SaveLog : SaveChangesInterceptor
    {
        public List<IReadOnlyList<string>> Passes { get; } = [];

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Passes.Add(Unsaved((TitanDbContext)eventData.Context!));
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// Each read is a millisecond after the last, so the rows of one handler call keep the order they were enqueued in.
    private sealed class TickingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _next = start;

        public override DateTimeOffset GetUtcNow()
        {
            DateTimeOffset now = _next;
            _next = now.AddMilliseconds(1);
            return now;
        }
    }

    private sealed class WarningLog<T>(List<string> warnings) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                warnings.Add(formatter(state, exception));
            }
        }
    }
}
