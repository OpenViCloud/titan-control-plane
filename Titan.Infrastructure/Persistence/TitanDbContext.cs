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
using Titan.Domain.Aggregates.FirewallRule;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.SshKey;
using Titan.Domain.Aggregates.ApiToken;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.VmSnapshot;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Aggregates.VolumeSnapshot;
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
    }

    private sealed class RateConverter : ValueConverter<Rate, decimal>
    {
        public RateConverter() : base(r => r.PerUnit, v => Rate.Of(v)) { }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("core");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TitanDbContext).Assembly);
    }
}
