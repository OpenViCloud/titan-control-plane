using Microsoft.EntityFrameworkCore;
using Npgsql;
using Titan.Domain.Aggregates.ClusterIpv6Pool;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Exceptions;

namespace Titan.Infrastructure.Endpoints;

/// The status and code of the error body { status, title, code } for an exception that reached the host.
public record ErrorStatus(int Status, string Code)
{
    public const string InternalErrorCode = "INTERNAL_ERROR";

    /// Two identical requests at the same instant both enqueue the same provider command key; the second commit hits
    /// the unique index (titan-ops architecture/provider-and-worker.md#provider-command-key). The first request did the work.
    public const string RequestInProgressCode = "REQUEST_IN_PROGRESS";
    public const string ProviderCommandKeyIndex = "IX_provider_commands_key";

    /// Two network creates in one cluster loaded the cluster's /64 pool at the same instant; the second commit finds its
    /// version moved or a key taken. Asking again picks another /64.
    public const string Ipv6AllocationConflictCode = "IPV6_ALLOCATION_CONFLICT";

    /// Two load balancer creates on one network picked the same free VIP at the same instant
    /// (titan-ops features/load-balancer/design.md#vip-in-network-64). Asking again picks another.
    public const string LoadBalancerVipConflictCode = "LOAD_BALANCER_VIP_CONFLICT";

    public static ErrorStatus For(Exception? ex) => ex switch
    {
        NotFoundException e            => new ErrorStatus(404, e.Code),
        ConflictException e            => new ErrorStatus(409, e.Code),
        ConcurrencyConflictException e => new ErrorStatus(409, e.Code),
        BusinessException e            => new ErrorStatus(422, e.Code),
        ForbiddenException e           => new ErrorStatus(403, e.Code),
        BadRequestException e          => new ErrorStatus(400, e.Code),
        ServiceUnavailableException e  => new ErrorStatus(503, e.Code),
        DbUpdateConcurrencyException e when TouchesAProviderCommand(e) => new ErrorStatus(409, RequestInProgressCode),
        DbUpdateException e when IsProviderCommandKeyClash(e) => new ErrorStatus(409, RequestInProgressCode),
        DbUpdateConcurrencyException e when TouchesAnIpv6Pool(e) => new ErrorStatus(409, Ipv6AllocationConflictCode),
        DbUpdateException e when IsAnIpv6PoolClash(e) => new ErrorStatus(409, Ipv6AllocationConflictCode),
        DbUpdateException e when IsUniqueViolationOf(e, Persistence.LoadBalancerConfiguration.VipIndexName) => new ErrorStatus(409, LoadBalancerVipConflictCode),
        _                              => new ErrorStatus(500, InternalErrorCode)
    };

    // Two identical repeats at once both reopen the same finished row: the version token lets one commit.
    private static bool TouchesAProviderCommand(DbUpdateConcurrencyException ex) =>
        ex.Entries.Any(entry => entry.Entity is ProviderCommand);

    private static bool IsProviderCommandKeyClash(DbUpdateException ex) => IsUniqueViolationOf(ex, ProviderCommandKeyIndex);

    // Two requests loaded one cluster's pool at the same version; the later commit finds it moved.
    private static bool TouchesAnIpv6Pool(DbUpdateConcurrencyException ex) =>
        ex.Entries.Any(entry => entry.Entity is ClusterIpv6Pool);

    // The first block given in a cluster created the pool row, or two requests wrote the same block, or one network
    // would hold two: all of them lose to the commit that came first, and asking again picks another block.
    private static bool IsAnIpv6PoolClash(DbUpdateException ex) =>
        IsUniqueViolationOf(ex, Persistence.ClusterIpv6PoolConfiguration.ClusterIndexName)
        || IsUniqueViolationOf(ex, Persistence.Ipv6Block64HoldingConfiguration.HoldingKeyName)
        || IsUniqueViolationOf(ex, Persistence.Ipv6Block64HoldingConfiguration.HolderIndexName);

    private static bool IsUniqueViolationOf(DbUpdateException ex, string indexName)
    {
        PostgresException? postgres = ex.InnerException as PostgresException;
        if (postgres == null)
        {
            return false;
        }
        return postgres.SqlState == PostgresErrorCodes.UniqueViolation && postgres.ConstraintName == indexName;
    }
}
