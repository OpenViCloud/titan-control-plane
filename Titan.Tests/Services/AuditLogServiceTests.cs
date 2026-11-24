using Moq;
using Titan.Application.Queries.Platform.AdminAuditLog.Contracts;
using AuditLogService = Titan.Application.Queries.Platform.AdminAuditLog.ListAdminAuditLogQueryHandler;
using Titan.Domain.Aggregates.AdminAuditLog;
using Titan.Domain.Repositories;

namespace Titan.Tests.Services;

public class AuditLogServiceTests
{
    private readonly Mock<IAdminAuditLogRepository> _repo = new();

    // Facade over ListAdminAuditLogQueryHandler so these tests keep one call shape.
    private AuditLogFacade Create() => new(new AuditLogService(_repo.Object));

    private sealed class AuditLogFacade(AuditLogService handler)
    {
        public Task<List<AdminAuditLogQueryResult>> ListAsync(int limit, CancellationToken ct = default)
            => handler.HandleAsync(new ListAdminAuditLogQuery(limit), ct);
    }

    private static AdminAuditLog Sample() =>
        AdminAuditLog.Create("vm.delete.cross_tenant", "admin-1", "vm", Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task ListAsync_MapsDto()
    {
        var row = Sample();
        _repo.Setup(r => r.ListAsync(AuditLogService.DefaultLimit, default)).ReturnsAsync(new List<AdminAuditLog> { row });

        var result = await Create().ListAsync(0, default);

        var dto = Assert.Single(result);
        Assert.Equal("vm.delete.cross_tenant", dto.Action);
        Assert.Equal("admin-1", dto.Actor);
        Assert.Equal("vm", dto.ResourceType);
        Assert.Equal(row.ResourceId, dto.ResourceId);
        Assert.Equal(row.OwnerTenantId, dto.OwnerTenantId);
    }

    // A row about a tenant's resource can say what the action read: a restore names its source and the archive
    // (titan-ops features/backup/design.md#restore-as-new-resource). Without a detail the field stays empty, as on every
    // row written before.
    [Fact]
    public async Task ListAsync_CarriesTheDetailOfATenantResourcesRow_AndNoneWhenNoneWasGiven()
    {
        Guid resourceId = Guid.NewGuid();
        Guid owner = Guid.NewGuid();
        var withDetail = AdminAuditLog.Create("vm.restore.cross_tenant", "admin-1", "vm", resourceId, owner, "source=abc backup=20261120T010000Z");
        _repo.Setup(r => r.ListAsync(AuditLogService.DefaultLimit, default)).ReturnsAsync([withDetail, Sample()]);

        var result = await Create().ListAsync(0, default);

        Assert.Equal("source=abc backup=20261120T010000Z", result[0].Detail);
        Assert.Equal((resourceId, owner, (string?)null), (result[0].ResourceId, result[0].OwnerTenantId, result[0].ResourceKey));
        Assert.Null(result[1].Detail);
    }

    [Theory]
    [InlineData(0, AuditLogService.DefaultLimit)]   // missing → default
    [InlineData(-3, AuditLogService.DefaultLimit)]  // invalid → default
    [InlineData(10, 10)]                            // valid → passthrough
    [InlineData(9999, AuditLogService.MaxLimit)]    // over cap → clamped
    public async Task ListAsync_ClampsLimit(int requested, int expected)
    {
        _repo.Setup(r => r.ListAsync(It.IsAny<int>(), default)).ReturnsAsync(new List<AdminAuditLog>());

        await Create().ListAsync(requested, default);

        _repo.Verify(r => r.ListAsync(expected, default), Times.Once);
    }
}
