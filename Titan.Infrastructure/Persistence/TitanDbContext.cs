using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Titan.Domain.Aggregates.Acceptance;
using Titan.Domain.Aggregates.ActivityEvent;
using Titan.Domain.Aggregates.AdminAuditLog;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.PaymentGatewayTransaction;
using Titan.Domain.Aggregates.TopUp;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.SshKey;
using Titan.Domain.Aggregates.ApiToken;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Aggregates.SnapshotOffering;
using Titan.Domain.Aggregates.VmOffering;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Aggregates.VolumeOffering;
using Titan.Domain.Values;

namespace Titan.Infrastructure.Persistence;

public class TitanDbContext(DbContextOptions<TitanDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Network> Networks => Set<Network>();
    public DbSet<FirewallRule> FirewallRules => Set<FirewallRule>();
    public DbSet<Vm> Vms => Set<Vm>();
    public DbSet<SshKey> SshKeys => Set<SshKey>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceItem> InvoiceItems => Set<InvoiceItem>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletTransaction> WalletTransactions => Set<WalletTransaction>();
    public DbSet<WalletMutation> WalletMutations => Set<WalletMutation>();
    public DbSet<TopUp> TopUps => Set<TopUp>();
    public DbSet<PaymentGatewayTransaction> PaymentGatewayTransactions => Set<PaymentGatewayTransaction>();
    public DbSet<VmSnapshot> VmSnapshots => Set<VmSnapshot>();
    public DbSet<Volume> Volumes => Set<Volume>();
    public DbSet<Domain.Aggregates.LoadBalancer.LoadBalancer> LoadBalancers =>
        Set<Domain.Aggregates.LoadBalancer.LoadBalancer>();
    public DbSet<VolumeSnapshot> VolumeSnapshots => Set<VolumeSnapshot>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<VmTemplate> VmTemplates => Set<VmTemplate>();
    public DbSet<VmOffering> VmOfferings => Set<VmOffering>();
    public DbSet<VolumeOffering> VolumeOfferings => Set<VolumeOffering>();
    public DbSet<SnapshotOffering> SnapshotOfferings => Set<SnapshotOffering>();
    public DbSet<Titan.Domain.Aggregates.BackupRecord.BackupRecord> Backups =>
        Set<Titan.Domain.Aggregates.BackupRecord.BackupRecord>();
    public DbSet<Titan.Domain.Aggregates.BackupObservation.BackupObservation> BackupObservations =>
        Set<Titan.Domain.Aggregates.BackupObservation.BackupObservation>();
    public DbSet<ProviderCommand> ProviderCommands => Set<ProviderCommand>();
    public DbSet<AdminAuditLog> AdminAuditLogs => Set<AdminAuditLog>();
    public DbSet<ActivityEvent> ActivityEvents => Set<ActivityEvent>();
    public DbSet<Acceptance> LegalAcceptances => Set<Acceptance>();

    // Money columns are bare `long` -> bigint with no converter (titan-ops architecture/money-and-wallet.md#money-is-long-vnd).
    // Rate's converter is a convention rather than per-property, so the next Rate property cannot forget it
    // (titan-ops architecture/money-and-wallet.md#rate-value-object). Rate.Of refuses a negative, so a negative
    // rate row throws on read instead of serving a corrupt price.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Rate>().HaveConversion<RateConverter>();
        // Same convention for the network's shape values (titan-ops architecture/code-structure.md#logic-placement):
        // a stored name or CIDR that breaks the rule throws on read instead of being served.
        configurationBuilder.Properties<NetworkName>().HaveConversion<NetworkNameConverter>();
        configurationBuilder.Properties<FirewallCidr>().HaveConversion<FirewallCidrConverter>();
    }

    private sealed class RateConverter : ValueConverter<Rate, decimal>
    {
        public RateConverter() : base(r => r.PerUnit, v => Rate.Of(v)) { }
    }

    private sealed class NetworkNameConverter : ValueConverter<NetworkName, string>
    {
        public NetworkNameConverter() : base(n => n.Value, v => NetworkName.Parse(v)) { }
    }

    private sealed class FirewallCidrConverter : ValueConverter<FirewallCidr, string>
    {
        public FirewallCidrConverter() : base(c => c.Value, v => FirewallCidr.Parse(v)) { }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("core");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TitanDbContext).Assembly);
    }

    private bool _ownsTransaction;

    /// Locks one aggregate root's row for the rest of this request (titan-ops architecture/code-structure.md#child-entity):
    /// an invariant counted on the root's children (a per-network cap) holds only if two writers take turns, and the
    /// row lock is where they queue. Opens a transaction when none is open; the next SaveChangesAsync commits it, so
    /// every repository's SaveAsync stays "one save, one commit", and a handler that throws before saving commits
    /// nothing (the transaction ends with the context). The statement is the caller's `SELECT … FOR UPDATE`; it is
    /// skipped on a provider without rows to lock (InMemory), so only a Postgres test measures the lock.
    public async Task LockRowAsync(FormattableString lockStatement, CancellationToken ct)
    {
        if (!Database.IsRelational())
        {
            return;
        }
        if (Database.CurrentTransaction == null)
        {
            await Database.BeginTransactionAsync(ct);
            _ownsTransaction = true;
        }
        await Database.ExecuteSqlInterpolatedAsync(lockStatement, ct);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        int written = await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
        bool thisContextOpenedTheTransaction = _ownsTransaction && Database.CurrentTransaction != null;
        if (thisContextOpenedTheTransaction)
        {
            await Database.CurrentTransaction!.CommitAsync(ct);
            _ownsTransaction = false;
        }
        return written;
    }
}
