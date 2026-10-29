using Microsoft.Extensions.DependencyInjection;
using Titan.Application.Options;
using Titan.Application.Services.Billing.Metering;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// One wiring path for the meters (titan-ops architecture/money-and-wallet.md#append-only-metering): MeterRegistrationTests
/// calls this exact method, so a dropped or extra meter is caught by test, not found as a hole in the invoice.
/// </summary>
public static class MeterRegistration
{
    public static IServiceCollection AddMeters(this IServiceCollection services)
    {
        services.AddOptions<BillingMeterOptions>().BindConfiguration(BillingMeterOptions.SectionName);

        // Liveness state is shared across per-tick scopes → singleton.
        services.AddSingleton<MeterHeartbeat>();

        // Also exposed as IMeter so the host can iterate them; the forwarders resolve the same scoped instance.
        services.AddScoped<IComputeMeterService, ComputeMeterService>();
        services.AddScoped<IDiskMeterService, DiskMeterService>();
        services.AddScoped<ISnapshotMeterService, SnapshotMeterService>();
        services.AddScoped<IMeter>(sp => sp.GetRequiredService<IComputeMeterService>());
        services.AddScoped<IMeter>(sp => sp.GetRequiredService<IDiskMeterService>());
        services.AddScoped<IMeter>(sp => sp.GetRequiredService<ISnapshotMeterService>());

        services.AddHostedService<MeterHostedService>();

        return services;
    }
}
