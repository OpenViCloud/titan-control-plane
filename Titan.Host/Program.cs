using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Application.Services.Catalog.VmTemplate;
using Titan.Application.Services.Platform;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Application.Services.Platform.Legal;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Queries.Compute.Vm;
// Each of these namespaces ends in a segment that is also an aggregate type name, so a plain `using`
// would make the bare name ambiguous (CS0118).
using ProviderCommandCmd = Titan.Application.Commands.Platform.ProviderCommand;
using ProviderCommandQry = Titan.Application.Queries.Platform.ProviderCommand;
using VolumeCmd = Titan.Application.Commands.Compute.Volume;
using VolumeQry = Titan.Application.Queries.Compute.Volume;
using LoadBalancerCmd = Titan.Application.Commands.Compute.LoadBalancer;
using LoadBalancerQry = Titan.Application.Queries.Compute.LoadBalancer;
using VmSnapCmd = Titan.Application.Commands.Compute.VmSnapshot;
using VmSnapQry = Titan.Application.Queries.Compute.VmSnapshot;
using VolSnapCmd = Titan.Application.Commands.Compute.VolumeSnapshot;
using VolSnapQry = Titan.Application.Queries.Compute.VolumeSnapshot;
using BillUsageQry = Titan.Application.Queries.Billing.UsageRecord;
using BillInvoiceQry = Titan.Application.Queries.Billing.Invoice;
using BillWalletQry = Titan.Application.Queries.Billing.Wallet;
using BillTopUpQry = Titan.Application.Queries.Billing.TopUp;
using BillTopUpCmd = Titan.Application.Commands.Billing.TopUp;
using BillInvoiceCmd = Titan.Application.Commands.Billing.Invoice;
using BillWalletCmd = Titan.Application.Commands.Billing.Wallet;
using BillPgtCmd = Titan.Application.Commands.Billing.PaymentGatewayTransaction;
using PlatTenantCmd = Titan.Application.Commands.Platform.Tenant;
using PlatLegalCmd = Titan.Application.Commands.Platform.Legal;
using PlatTenantQry = Titan.Application.Queries.Platform.Tenant;
using PlatLegalQry = Titan.Application.Queries.Platform.Legal;
using PlatActivityQry = Titan.Application.Queries.Platform.ActivityEvent;
using PlatAuditQry = Titan.Application.Queries.Platform.AdminAuditLog;
using NetNetworkCmd = Titan.Application.Commands.Networking.Network;
using NetFirewallCmd = Titan.Application.Commands.Networking.FirewallRule;
using CompSshKeyCmd = Titan.Application.Commands.Compute.SshKey;
using CompSshKeyQry = Titan.Application.Queries.Compute.SshKey;
using PlatApiTokenCmd = Titan.Application.Commands.Platform.ApiToken;
using PlatApiTokenQry = Titan.Application.Queries.Platform.ApiToken;
using NetNetworkQry = Titan.Application.Queries.Networking.Network;
using NetFirewallQry = Titan.Application.Queries.Networking.FirewallRule;
using CatTemplateCmd = Titan.Application.Commands.Catalog.VmTemplate;
using CatOfferingCmd = Titan.Application.Commands.Catalog.VmOffering;
using CatTemplateQry = Titan.Application.Queries.Catalog.VmTemplate;
using CatOfferingQry = Titan.Application.Queries.Catalog.VmOffering;
using CatVolumeOfferingQry = Titan.Application.Queries.Catalog.VolumeOffering;
using CatZoneQry = Titan.Application.Queries.Catalog.Zone;
using CatOsImageQry = Titan.Application.Queries.Catalog.OsImage;
using AdminVmCmd = Titan.Application.Commands.Admin.Vm;
using AdminVmQry = Titan.Application.Queries.Admin.Vm;
using AdminNetworkQry = Titan.Application.Queries.Admin.Network;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Endpoints;
using Titan.Domain.Repositories.Billing;
using Titan.Domain.Repositories.Catalog;
using Titan.Domain.Repositories.Compute;
using Titan.Domain.Repositories.Networking;
using Titan.Domain.Repositories.Platform;
using Titan.Infrastructure.Auth;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Messaging;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

var builder = WebApplication.CreateBuilder(args);

// The framework's exception middleware logs every exception with its stack trace, an expected 4xx included (the
// worker's empty lease poll is one every few seconds). A 500 keeps its log: UseExceptionHandler below writes it.
builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogLevel.None);

builder.Services.AddDbContext<TitanDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Titan")));

var connectionString = builder.Configuration.GetConnectionString("Titan")!;
builder.Services.AddHealthChecks()
    .AddAsyncCheck("database", async ct =>
    {
        await using var conn = new NpgsqlConnection(connectionString);
        try
        {
            await conn.OpenAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch
        {
            return HealthCheckResult.Unhealthy();
        }
    })
    // A meter that stops ticking under-bills silently. Tagged to stay off the readiness probe: a stale
    // meter is a billing signal and must never pull the API pod out of rotation.
    .AddCheck<MeterLivenessHealthCheck>("meters", tags: ["meters"])
    // A stuck wallet mutation means a payment is waiting; failing readiness over it would turn a delayed
    // payment into an outage (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/money-and-wallet.md#single-writer-outbox).
    .AddCheck<WalletMutationHealthCheck>("wallet-async", tags: ["background", "wallet"])
    // A stale reconciler means observations are late, not that this pod cannot serve.
    .AddCheck<ReconcilerLivenessHealthCheck>("reconcilers", tags: ["background", "reconcilers"]);

builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<INetworkRepository, NetworkRepository>();
builder.Services.AddScoped<IFirewallRuleRepository, FirewallRuleRepository>();
builder.Services.AddScoped<ISshKeyRepository, SshKeyRepository>();
builder.Services.AddScoped<IApiTokenRepository, ApiTokenRepository>();
builder.Services.AddScoped<IVmRepository, VmRepository>();
builder.Services.AddScoped<ISnapshotOfferingRepository, SnapshotOfferingRepository>();
builder.Services.AddScoped<IUsageRecordRepository, UsageRecordRepository>();
builder.Services.AddScoped<IInvoiceRepository, InvoiceRepository>();
builder.Services.AddScoped<ICreditRepository, CreditRepository>();
builder.Services.AddScoped<IWalletRepository, WalletRepository>();
builder.Services.AddScoped<IWalletMutationRepository, WalletMutationRepository>();
builder.Services.AddScoped<IPaymentGatewayTransactionRepository, PaymentGatewayTransactionRepository>();
builder.Services.AddScoped<IVmSnapshotRepository, VmSnapshotRepository>();
builder.Services.AddScoped<IVolumeRepository, VolumeRepository>();
builder.Services.AddScoped<ILoadBalancerRepository, LoadBalancerRepository>();
builder.Services.AddScoped<IVolumeSnapshotRepository, VolumeSnapshotRepository>();
builder.Services.AddScoped<IVmTemplateRepository, VmTemplateRepository>();
builder.Services.AddScoped<IVmOfferingRepository, VmOfferingRepository>();
builder.Services.AddScoped<IVolumeOfferingRepository, VolumeOfferingRepository>();
builder.Services.AddScoped<IAdminAuditLogRepository, AdminAuditLogRepository>();
builder.Services.AddScoped<IActivityEventRepository, ActivityEventRepository>();
builder.Services.AddScoped<IAcceptanceRepository, AcceptanceRepository>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<TenantResolutionMiddleware>();
builder.Services.AddScoped<NetNetworkCmd.CreateNetworkCommandHandler>();
builder.Services.AddScoped<NetNetworkCmd.RenameNetworkCommandHandler>();
builder.Services.AddScoped<NetNetworkCmd.DeleteNetworkCommandHandler>();
builder.Services.AddScoped<NetNetworkCmd.UpdateNetworkStatusCommandHandler>();
builder.Services.AddScoped<NetFirewallCmd.CreateFirewallRuleCommandHandler>();
builder.Services.AddScoped<NetFirewallCmd.DeleteFirewallRuleCommandHandler>();
builder.Services.AddScoped<NetFirewallCmd.UpdateFirewallRuleStatusCommandHandler>();
builder.Services.AddScoped<NetNetworkQry.ListNetworksQueryHandler>();
builder.Services.AddScoped<NetNetworkQry.GetNetworkQueryHandler>();
builder.Services.AddScoped<NetNetworkQry.ListReconcilableNetworksQueryHandler>();
builder.Services.AddScoped<NetFirewallQry.ListFirewallRulesQueryHandler>();
builder.Services.AddScoped<NetFirewallQry.ListReconcilableFirewallRulesQueryHandler>();
builder.Services.AddScoped<CompSshKeyCmd.AddSshKeyCommandHandler>();
builder.Services.AddScoped<CompSshKeyCmd.DeleteSshKeyCommandHandler>();
builder.Services.AddScoped<CompSshKeyQry.ListSshKeysQueryHandler>();
builder.Services.AddScoped<PlatApiTokenCmd.CreateApiTokenCommandHandler>();
builder.Services.AddScoped<PlatApiTokenCmd.DeleteApiTokenCommandHandler>();
builder.Services.AddScoped<PlatApiTokenQry.ListApiTokensQueryHandler>();
// Handlers are registered by hand, one line each (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#no-mediatr): this
// list is the greppable inventory of what exists, which an assembly scan would delete.
builder.Services.AddScoped<ListVmsQueryHandler>();
builder.Services.AddScoped<GetVmQueryHandler>();
builder.Services.AddScoped<GetVmShellTargetQueryHandler>();
builder.Services.AddScoped<RequestVmSshCommandHandler>();
builder.Services.AddScoped<CreateVmCommandHandler>();
builder.Services.AddScoped<RenameVmCommandHandler>();
builder.Services.AddScoped<DeleteVmCommandHandler>();
builder.Services.AddScoped<StopVmCommandHandler>();
builder.Services.AddScoped<StartVmCommandHandler>();
builder.Services.AddScoped<UpdateVmStatusCommandHandler>();
builder.Services.AddScoped<MarkVmIntentFailedCommandHandler>();
builder.Services.AddScoped<IActivityRecorder, ActivityRecorder>();
builder.Services.AddScoped<BillUsageQry.GetVmCostQueryHandler>();
builder.Services.AddScoped<BillUsageQry.GetTenantBillingSummaryQueryHandler>();
builder.Services.AddScoped<BillInvoiceQry.ListTenantInvoicesQueryHandler>();
builder.Services.AddScoped<BillInvoiceQry.GetTenantInvoiceQueryHandler>();
builder.Services.AddScoped<BillInvoiceQry.ListInvoicesQueryHandler>();
builder.Services.AddScoped<BillInvoiceQry.GetReceivablesQueryHandler>();
builder.Services.AddScoped<BillWalletQry.GetWalletQueryHandler>();
builder.Services.AddScoped<BillWalletQry.GetWalletStatementQueryHandler>();
builder.Services.AddScoped<BillTopUpQry.ListMyTopUpsQueryHandler>();
builder.Services.AddScoped<BillTopUpQry.ListTopUpsQueryHandler>();
builder.Services.AddScoped<BillTopUpCmd.RequestTopUpCommandHandler>();
builder.Services.AddScoped<BillTopUpCmd.GrantTopUpCommandHandler>();
builder.Services.AddScoped<BillTopUpCmd.ConfirmTopUpCommandHandler>();
builder.Services.AddScoped<BillInvoiceCmd.MarkInvoicePaidCommandHandler>();
builder.Services.AddScoped<BillInvoiceCmd.PayInvoiceFromWalletCommandHandler>();
builder.Services.AddScoped<BillInvoiceCmd.SettleInvoiceFromWalletCommandHandler>();
builder.Services.AddScoped<BillInvoiceCmd.GenerateInvoicesForPeriodCommandHandler>();
builder.Services.AddScoped<BillWalletCmd.OpenWalletCommandHandler>();
builder.Services.AddScoped<BillPgtCmd.ReceiveSePayPaymentCommandHandler>();
// Scoped: the subscriber opens a scope per notification, the sweeper one per pass
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/money-and-wallet.md#single-writer-outbox).
builder.Services.AddScoped<IWalletMutationConsumer, WalletMutationConsumer>();
// https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/legal/design.md#consent-gate-per-user
builder.Services.AddSingleton<ILegalDocumentCatalog, LegalDocumentCatalog>();
// BootstrapTenant and ApproveVerifiedSignup have no endpoint: TenantResolutionMiddleware injects them on
// every authenticated request, so a missing line here fails every signed-in call, not just one route.
builder.Services.AddScoped<PlatTenantCmd.BootstrapTenantCommandHandler>();
builder.Services.AddScoped<PlatTenantCmd.ApproveVerifiedSignupCommandHandler>();
builder.Services.AddScoped<PlatTenantCmd.UpdateTenantStatusCommandHandler>();
builder.Services.AddScoped<PlatTenantCmd.UpdateTenantQuotaCommandHandler>();
builder.Services.AddScoped<PlatTenantCmd.UpdateTenantDebtCeilingCommandHandler>();
builder.Services.AddScoped<PlatLegalCmd.RecordAcceptanceCommandHandler>();
builder.Services.AddScoped<PlatTenantQry.ListTenantsQueryHandler>();
builder.Services.AddScoped<PlatTenantQry.GetTenantQueryHandler>();
builder.Services.AddScoped<PlatTenantQry.ListTenantMembersQueryHandler>();
builder.Services.AddScoped<PlatLegalQry.GetLegalDocumentQueryHandler>();
builder.Services.AddScoped<PlatLegalQry.GetConsentStatusQueryHandler>();
builder.Services.AddScoped<PlatActivityQry.ListActivityEventsQueryHandler>();
builder.Services.AddScoped<PlatAuditQry.ListAdminAuditLogQueryHandler>();
// A cluster no handshake could reach throws here, so the host does not start (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#cluster-from-config).
var zoneOptions = ZoneOptions.FromConfiguration(
    builder.Configuration.GetSection(ZoneOptions.SectionName).Get<List<ZoneConfig>>());
builder.Services.AddSingleton(zoneOptions);

var creditOptions = builder.Configuration.GetSection(CreditOptions.SectionName).Get<CreditOptions>() ?? new CreditOptions();
builder.Services.AddSingleton(creditOptions);

// SePay__ApiKey comes from the environment; left empty (dev), the webhook answers 401.
var sePayOptions = builder.Configuration.GetSection(SePayOptions.SectionName).Get<SePayOptions>() ?? new SePayOptions();
builder.Services.AddSingleton(sePayOptions);

// NATS is off by default: the outbox table is the durable queue and NATS only a speed layer, so a missed
// notify is a delayed apply, never lost money (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/money-and-wallet.md#single-writer-outbox).
// At control-plane replicas: 1 the single subscriber is the one writer per wallet.
var natsOptions = builder.Configuration.GetSection(NatsOptions.SectionName).Get<NatsOptions>() ?? new NatsOptions();
builder.Services.AddSingleton(natsOptions);
if (natsOptions.IsActive)
{
    builder.Services.AddSingleton<NatsWalletMutationBus>();
    builder.Services.AddSingleton<IWalletMutationPublisher>(sp => sp.GetRequiredService<NatsWalletMutationBus>());
    builder.Services.AddSingleton<IWalletMutationSubscription>(sp => sp.GetRequiredService<NatsWalletMutationBus>());
    builder.Services.AddHostedService<WalletMutationSubscriber>();
}
else
{
    builder.Services.AddSingleton<IWalletMutationPublisher, NullWalletMutationPublisher>();
}

builder.Services.AddScoped<VolumeCmd.CreateVolumeCommandHandler>();
builder.Services.AddScoped<VolumeCmd.AttachVolumeCommandHandler>();
builder.Services.AddScoped<VolumeCmd.DetachVolumeCommandHandler>();
builder.Services.AddScoped<VolumeCmd.DeleteVolumeCommandHandler>();
builder.Services.AddScoped<VolumeCmd.UpdateVolumeStatusCommandHandler>();
builder.Services.AddScoped<VolumeQry.ListVolumesQueryHandler>();
builder.Services.AddScoped<VolumeQry.ListReconcilableStorageQueryHandler>();
builder.Services.AddScoped<LoadBalancerCmd.CreateLoadBalancerCommandHandler>();
builder.Services.AddScoped<LoadBalancerCmd.UpdateLoadBalancerCommandHandler>();
builder.Services.AddScoped<LoadBalancerCmd.DeleteLoadBalancerCommandHandler>();
builder.Services.AddScoped<LoadBalancerCmd.RenameLoadBalancerCommandHandler>();
builder.Services.AddScoped<LoadBalancerCmd.UpdateLoadBalancerStatusCommandHandler>();
builder.Services.AddScoped<LoadBalancerQry.ListLoadBalancersQueryHandler>();
builder.Services.AddScoped<LoadBalancerQry.GetLoadBalancerQueryHandler>();
builder.Services.AddScoped<LoadBalancerQry.ListReconcilableLoadBalancersQueryHandler>();
builder.Services.AddScoped<VmSnapCmd.CreateVmSnapshotCommandHandler>();
builder.Services.AddScoped<VmSnapCmd.RevertVmSnapshotCommandHandler>();
builder.Services.AddScoped<VmSnapCmd.DeleteVmSnapshotCommandHandler>();
builder.Services.AddScoped<VmSnapCmd.UpdateVmSnapshotStatusCommandHandler>();
builder.Services.AddScoped<VmSnapQry.ListVmSnapshotsQueryHandler>();
builder.Services.AddScoped<VmSnapQry.ListReconcilableVmSnapshotsQueryHandler>();
builder.Services.AddScoped<ListReconcilableVmsQueryHandler>();
builder.Services.AddScoped<VolSnapCmd.CreateVolumeSnapshotCommandHandler>();
builder.Services.AddScoped<VolSnapCmd.DeleteVolumeSnapshotCommandHandler>();
builder.Services.AddScoped<VolSnapCmd.RestoreVolumeSnapshotCommandHandler>();
builder.Services.AddScoped<VolSnapCmd.UpdateVolumeSnapshotStatusCommandHandler>();
builder.Services.AddScoped<VolSnapQry.ListVolumeSnapshotsQueryHandler>();
builder.Services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
builder.Services.AddScoped<IIdempotencyService, IdempotencyService>();
builder.Services.AddScoped<IdempotencyMiddleware>();
builder.Services.AddOptions<IdempotencyCleanupOptions>().BindConfiguration(IdempotencyCleanupOptions.SectionName);
builder.Services.AddHostedService<IdempotencyCleanupService>();
// The sweeper is on by default, unlike the bus: it is the only thing that makes a non-persistent bus safe,
// and nothing alerts on its absence (/healthz/wallet only reports it). Its SKIP LOCKED does not keep it off
// a mutation the subscriber is applying (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/billing/wallet/design.md#wallet-debts).
builder.Services.AddOptions<WalletSweeperOptions>().BindConfiguration(WalletSweeperOptions.SectionName);
// Singleton: written by the sweeper loop, read by the health check.
builder.Services.AddSingleton<WalletSweeperHeartbeat>();
builder.Services.AddHostedService<WalletMutationSweeper>();
// The worker's reconcilers poll GET /internal/reconcile/*; each poll feeds this heartbeat, read by /healthz/reconcilers.
builder.Services.AddOptions<ReconcilerLivenessOptions>().BindConfiguration(ReconcilerLivenessOptions.SectionName);
builder.Services.AddSingleton<ReconcilerHeartbeat>();
// https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/money-and-wallet.md#append-only-metering
builder.Services.AddMeters();
builder.Services.AddScoped<CatTemplateCmd.SyncTemplatesCommandHandler>();
builder.Services.AddScoped<CatTemplateCmd.UpdateTemplateStatusCommandHandler>();
builder.Services.AddScoped<CatOfferingCmd.UpdateVmOfferingPriceCommandHandler>();
builder.Services.AddScoped<CatTemplateQry.ListTemplatesQueryHandler>();
builder.Services.AddScoped<CatTemplateQry.ListAdminTemplatesQueryHandler>();
builder.Services.AddScoped<CatTemplateQry.ListReconcilableTemplatesQueryHandler>();
builder.Services.AddScoped<CatOfferingQry.ListVmOfferingsQueryHandler>();
builder.Services.AddScoped<CatVolumeOfferingQry.ListVolumeOfferingsQueryHandler>();
builder.Services.AddScoped<CatZoneQry.ListZonesQueryHandler>();
builder.Services.AddScoped<CatOsImageQry.ListOsImagesQueryHandler>();
builder.Services.AddScoped<TemplateSyncRunner>();
// Cross-tenant, platform-admin only (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/tenancy-and-admin.md#admin-cross-tenant-read).
builder.Services.AddScoped<AdminVmCmd.DeleteAdminVmCommandHandler>();
builder.Services.AddScoped<AdminVmQry.ListAdminVmsQueryHandler>();
builder.Services.AddScoped<AdminNetworkQry.ListAdminNetworksQueryHandler>();
// The provider command table (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/provider-and-worker.md#provider-command-table): handlers stage rows,
// the worker leases them over /internal.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IProviderCommandRepository, ProviderCommandRepository>();
builder.Services.AddScoped<IProviderCommandQueue, ProviderCommandQueue>();
builder.Services.AddScoped<ProviderCommandCmd.LeaseProviderCommandCommandHandler>();
builder.Services.AddScoped<ProviderCommandCmd.ExtendProviderCommandLeaseCommandHandler>();
builder.Services.AddScoped<ProviderCommandCmd.CompleteProviderCommandCommandHandler>();
builder.Services.AddScoped<ProviderCommandCmd.FailProviderCommandCommandHandler>();
builder.Services.AddScoped<ProviderCommandQry.GetLeasedProviderCommandQueryHandler>();
// Validated on start so a broken Secret, or a configured cluster without one, stops startup rather than every Incus call
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#provider-client-identity).
builder.Services.AddOptions<Titan.Infrastructure.Incus.ProviderClientIdentityOptions>()
    .BindConfiguration(Titan.Infrastructure.Incus.ProviderClientIdentityOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<Titan.Infrastructure.Incus.ProviderClientIdentityOptions>>(
    new Titan.Infrastructure.Incus.ProviderClientIdentityValidation(clustersConfigured: zoneOptions.Zones.Count > 0));
builder.Services.AddOptions<Titan.Application.Options.BackupTargetOptions>()
    .BindConfiguration(Titan.Application.Options.BackupTargetOptions.SectionName);
builder.Services.AddSingleton<IBackupBucketFactory, Titan.Infrastructure.Backup.S3BackupBucketFactory>();
builder.Services.AddScoped<Titan.Application.Commands.Platform.BackupTarget.TestBackupTargetCommandHandler>();
builder.Services.AddScoped<Titan.Domain.Repositories.Platform.IBackupRecordRepository,
    Titan.Infrastructure.Repositories.BackupRecordRepository>();
builder.Services.AddScoped<Titan.Domain.Repositories.Platform.IBackupObservationRepository,
    Titan.Infrastructure.Repositories.BackupObservationRepository>();
builder.Services.AddScoped<Titan.Application.Commands.Platform.Backup.RefreshBackupStatusCommandHandler>();
builder.Services.AddScoped<Titan.Application.Queries.Platform.Backup.ListBackupsQueryHandler>();
builder.Services.AddScoped<Titan.Application.Queries.Admin.Backup.ListDiskBackupsQueryHandler>();
builder.Services.AddOptions<Titan.Application.Options.BackupStatusOptions>()
    .BindConfiguration(Titan.Application.Options.BackupStatusOptions.SectionName);
builder.Services.AddHostedService<Titan.Infrastructure.Hosting.BackupStatusRefreshService>();
builder.Services.AddScoped<Titan.Application.Commands.Admin.Backup.ScheduleBackupsCommandHandler>();
builder.Services.AddHostedService<Titan.Infrastructure.Hosting.BackupScheduleService>();
builder.Services.AddScoped<Titan.Application.Queries.Platform.BackupTarget.GetBackupTargetQueryHandler>();
builder.Services.AddSingleton<Titan.Infrastructure.Incus.ProviderClientIdentityStore>();
builder.Services.AddSingleton<IProviderClientIdentityStore>(sp =>
    sp.GetRequiredService<Titan.Infrastructure.Incus.ProviderClientIdentityStore>());
builder.Services.AddScoped<Titan.Infrastructure.Incus.IncusClientFactory>();
builder.Services.AddScoped<IComputeCatalogProvider, Titan.Infrastructure.Incus.IncusCatalogProvider>();
builder.Services.AddScoped<Titan.Infrastructure.Incus.IncusShellConnector>();
// One count for the whole process: right while the control plane runs one replica (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/vm/design.md#ssh-session).
builder.Services.AddSingleton<Titan.Infrastructure.Sessions.ShellSessionLimiter>();
builder.Services.AddScoped<IClusterConnectionTester, Titan.Infrastructure.Incus.IncusClusterConnectionTester>();
builder.Services.AddScoped<IAdminClusterService, Titan.Infrastructure.Incus.AdminClusterService>();
// FastEndpoints discovers endpoints and validators from Titan.Infrastructure automatically
builder.Services.AddFastEndpoints();

// Internal worker callbacks and admin/tenant-lifecycle routes are not part of the public Terraform/CLI
// surface, so the spec leaves them out.
builder.Services.SwaggerDocument(o =>
{
    // Without this NSwag names every schema after its namespace, so a folder move inside Titan.Application
    // breaks the published contract (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/http-contract.md#openapi-schema-names).
    o.ShortSchemaNames = true;
    o.DocumentSettings = s =>
    {
        s.DocumentName = "v1";
        s.Title = "Titan API";
        s.Version = "v1";
        // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/http-contract.md#idempotency-key: a create refuses a request without Idempotency-Key, so the published contract says
        // so — otherwise a client generated from this spec would send requests the server rejects.
        s.AddOperationFilter(AddRequiredIdempotencyKeyHeader);
        // https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/public-api/design.md#state-enums: a nullable enum field comes out as `oneOf`, which
        // oapi-codegen turns into a wrapper type; `allOf` gives the client a plain pointer to the enum.
        s.DocumentProcessors.Add(new NullableReferenceAsAllOf());
    };
    o.EndpointFilter = ep =>
    {
        var route = ep.Routes?.FirstOrDefault() ?? string.Empty;
        return !route.StartsWith("internal", StringComparison.OrdinalIgnoreCase)
            && !route.StartsWith("v1/tenants", StringComparison.OrdinalIgnoreCase)
            && !route.StartsWith("v1/admin", StringComparison.OrdinalIgnoreCase);
    };
});

const string corsPolicyName = "TitanCors";
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
    options.AddPolicy(corsPolicyName, policy =>
    {
        if (allowedOrigins.Length > 0)
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
    }));

// Two schemes behind one selector: an ovc_ bearer is an API token, anything else is a Keycloak JWT
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/public-api/design.md#api-token). With two schemes there is no implicit default any more.
builder.Services.AddAuthentication(ApiTokenSchemes.Selector)
    .AddPolicyScheme(ApiTokenSchemes.Selector, ApiTokenSchemes.Selector,
        options => options.ForwardDefaultSelector = ApiTokenSchemes.Choose)
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiTokenSchemes.ApiToken, null)
    .AddJwtBearer(options =>
{
    options.Authority = builder.Configuration["Keycloak:Authority"];
    options.Audience = builder.Configuration["Keycloak:Audience"];
    options.MapInboundClaims = false; // preserve raw JWT claim names (e.g. "email", "sub")
});

// Keycloak realm roles live in a nested realm_access.roles claim — project them into
// role claims so Roles("platform-admin")/IsInRole work (else admin endpoints 403 even a
// valid platform-admin token).
builder.Services.AddScoped<IClaimsTransformation, KeycloakRealmRolesClaimsTransformation>();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("PlatformAdmin", policy =>
        policy.RequireRole("platform-admin"));

var app = builder.Build();

// One-shot CLI: sync every template, exit non-zero unless all are Synced. Nothing calls it since the PostSync Job
// was removed (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md, debt table).
if (args.Contains("sync-templates"))
{
    using var scope = app.Services.CreateScope();
    var runner = scope.ServiceProvider.GetRequiredService<TemplateSyncRunner>();
    var result = await runner.RunAsync(
        maxAttempts: 30,
        pollInterval: TimeSpan.FromSeconds(10),
        delay: Task.Delay);
    return result.Outcome == TemplateSyncOutcome.AllSynced ? 0 : 1;
}

app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    ErrorStatus error = ErrorStatus.For(ex);
    int status = error.Status;
    string code = error.Code;

    if (status == 500)
        ctx.RequestServices.GetRequiredService<ILogger<Program>>()
            .LogError(ex, "Unhandled exception {Method} {Path}", ctx.Request.Method, ctx.Request.Path);

    await JsonErrorWriter.WriteAsync(ctx.Response, status, new { status, title = ex?.Message, code }, ctx.RequestAborted);
}));

app.UseCors(corsPolicyName);

// SSH sessions (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/vm/design.md#ssh-session): a keep-alive frame every 30 s keeps an idle session's path open.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthorization();
app.UseMiddleware<IdempotencyMiddleware>();

app.UseFastEndpoints(c =>
{
    c.Endpoints.Configurator = ep => ep.Options(x => x.RequireAuthorization());
    c.Errors.ResponseBuilder = (failures, ctx, statusCode) => new
    {
        status = statusCode,
        title = string.Join("; ", failures.Select(f => f.ErrorMessage)),
        code = "VALIDATION_ERROR"
    };
});

app.UseSwaggerGen();

// Readiness answers "can this pod serve requests"; a background loop falling behind does not change that,
// and at replicas: 1 failing readiness takes the whole service down. Excluded by tag, not by name, so a
// new check tagged "background" inherits it; an untagged check lands on readiness.
app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    Predicate = check => !check.Tags.Contains("meters") && !check.Tags.Contains("background")
}).AllowAnonymous();

app.MapHealthChecks("/healthz/meters", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("meters")
}).AllowAnonymous();

// Nothing polls this: no Prometheus, no livenessProbe (https://github.com/OpenViCloud/titan-ops/blob/main/docs/infra/runbooks/runbook-wallet-async.md). It is a
// question someone has to ask, not an alert.
app.MapHealthChecks("/healthz/wallet", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("wallet")
}).AllowAnonymous();

// Like /healthz/wallet, nothing polls this.
app.MapHealthChecks("/healthz/reconcilers", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("reconcilers")
}).AllowAnonymous();

app.Run();

return 0;

// Uses IRequiresIdempotencyKey, the same marker the middleware enforces (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/http-contract.md#idempotency-key).
static bool AddRequiredIdempotencyKeyHeader(NSwag.Generation.Processors.Contexts.OperationProcessorContext context)
{
    var aspNetContext = context as NSwag.Generation.AspNetCore.AspNetCoreOperationProcessorContext;
    if (aspNetContext == null)
    {
        return true;
    }

    EndpointDefinition? definition = aspNetContext.ApiDescription.ActionDescriptor.EndpointMetadata
        .OfType<EndpointDefinition>()
        .FirstOrDefault();
    if (definition == null)
    {
        return true;
    }

    bool requiresKey = typeof(IRequiresIdempotencyKey).IsAssignableFrom(definition.EndpointType);
    if (!requiresKey)
    {
        return true;
    }

    context.OperationDescription.Operation.Parameters.Add(new NSwag.OpenApiParameter
    {
        Name = "Idempotency-Key",
        Kind = NSwag.OpenApiParameterKind.Header,
        IsRequired = true,
        Schema = new NJsonSchema.JsonSchema { Type = NJsonSchema.JsonObjectType.String },
        Description = "One value per logical submission; a retry with the same value replays the first response.",
    });
    return true; // keep the operation in the document
}

public partial class Program { }

class NullableReferenceAsAllOf : NSwag.Generation.Processors.IDocumentProcessor
{
    public void Process(NSwag.Generation.Processors.Contexts.DocumentProcessorContext context)
    {
        foreach (NJsonSchema.JsonSchema schema in context.Document.Components.Schemas.Values)
        {
            foreach (NJsonSchema.JsonSchemaProperty property in schema.Properties.Values)
            {
                bool isNullableReference = property.IsNullableRaw == true && property.OneOf.Count == 1;
                if (!isNullableReference)
                {
                    continue;
                }
                NJsonSchema.JsonSchema reference = property.OneOf.Single();
                property.OneOf.Clear();
                property.AllOf.Add(reference);
            }
        }
    }
}
