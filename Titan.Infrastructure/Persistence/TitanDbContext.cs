using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Titan.Domain.Aggregates.Platform.Acceptance;
using Titan.Domain.Aggregates.Platform.ActivityEvent;
using Titan.Domain.Aggregates.Platform.AdminAuditLog;
using Titan.Domain.Aggregates.Platform.ProviderClientIdentity;
using Titan.Domain.Aggregates.Platform.ProviderCommand;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Aggregates.Billing.Invoice;
using Titan.Domain.Aggregates.Billing.PaymentGatewayTransaction;
using Titan.Domain.Aggregates.Billing.TopUp;
using Titan.Domain.Aggregates.Billing.UsageRecord;
using Titan.Domain.Aggregates.Billing.Wallet;
using Titan.Domain.Aggregates.Billing.WalletMutation;
using Titan.Domain.Aggregates.Networking.FirewallRule;
using Titan.Domain.Aggregates.Networking.Network;
using Titan.Domain.Aggregates.Compute.SshKey;
using Titan.Domain.Aggregates.Platform.ApiToken;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Aggregates.Compute.VmSnapshot;
using Titan.Domain.Aggregates.Compute.Volume;
using Titan.Domain.Aggregates.Compute.VolumeSnapshot;
using Titan.Domain.Aggregates.Catalog.SnapshotOffering;
using Titan.Domain.Aggregates.Catalog.VmOffering;
using Titan.Domain.Aggregates.Catalog.VmTemplate;
using Titan.Domain.Aggregates.Catalog.VolumeOffering;
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
    public DbSet<Domain.Aggregates.Compute.LoadBalancer.LoadBalancer> LoadBalancers =>
        Set<Domain.Aggregates.Compute.LoadBalancer.LoadBalancer>();
    public DbSet<VolumeSnapshot> VolumeSnapshots => Set<VolumeSnapshot>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<VmTemplate> VmTemplates => Set<VmTemplate>();
    public DbSet<VmOffering> VmOfferings => Set<VmOffering>();
    public DbSet<VolumeOffering> VolumeOfferings => Set<VolumeOffering>();
    public DbSet<SnapshotOffering> SnapshotOfferings => Set<SnapshotOffering>();
    public DbSet<Titan.Domain.Aggregates.Platform.Cluster.Cluster> Clusters => Set<Titan.Domain.Aggregates.Platform.Cluster.Cluster>();
    public DbSet<ProviderClientIdentity> ProviderClientIdentities => Set<ProviderClientIdentity>();
    public DbSet<Titan.Domain.Aggregates.Platform.BackupTarget.BackupTarget> BackupTargets =>
        Set<Titan.Domain.Aggregates.Platform.BackupTarget.BackupTarget>();
    public DbSet<Titan.Domain.Aggregates.Platform.BackupRecord.BackupRecord> Backups =>
        Set<Titan.Domain.Aggregates.Platform.BackupRecord.BackupRecord>();
    public DbSet<Titan.Domain.Aggregates.Platform.BackupObservation.BackupObservation> BackupObservations =>
        Set<Titan.Domain.Aggregates.Platform.BackupObservation.BackupObservation>();
    public DbSet<ProviderCommand> ProviderCommands => Set<ProviderCommand>();
    public DbSet<AdminAuditLog> AdminAuditLogs => Set<AdminAuditLog>();
    public DbSet<ActivityEvent> ActivityEvents => Set<ActivityEvent>();
    public DbSet<Acceptance> LegalAcceptances => Set<Acceptance>();

    // Money columns are bare `long` -> bigint with no converter (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/money-and-wallet.md#money-is-long-vnd).
    // Rate's converter is a convention rather than per-property, so the next Rate property cannot forget it
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/money-and-wallet.md#rate-value-object). Rate.Of refuses a negative, so a negative
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
