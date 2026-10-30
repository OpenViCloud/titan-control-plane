using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Queries.Platform.ActivityEvent.Contracts;
using ActivityService = Titan.Application.Queries.Platform.ActivityEvent.ListActivityEventsQueryHandler;
using Titan.Domain.Aggregates.ActivityEvent;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Tests.Services;

public class ActivityServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private readonly Mock<IActivityEventRepository> _repo = new();
    private readonly Mock<ICurrentUser> _currentUser = new();

    // Facade over ListActivityEventsQueryHandler so these tests keep one call shape.
    private ActivityFacade Create() => new(new ActivityService(_repo.Object, _currentUser.Object));

    private sealed class ActivityFacade(ActivityService handler)
    {
        public Task<List<ActivityEventQueryResult>> ListAsync(int limit, CancellationToken ct = default)
            => handler.HandleAsync(new ListActivityEventsQuery(limit), ct);
    }

    private static ActivityEvent SampleEvent(string name = "web-01") =>
        ActivityEvent.Create(TenantId, "vm.created", "vm", Guid.NewGuid(), name, "kc-1");

    [Fact]
    public async Task ListAsync_ScopesToCallerTenant_MapsDto()
    {
        _currentUser.Setup(u => u.TenantId).Returns(TenantId);
        _repo.Setup(r => r.ListByTenantAsync(TenantId, ActivityService.DefaultLimit, default))
            .ReturnsAsync(new List<ActivityEvent> { SampleEvent("api-1") });

        var result = await Create().ListAsync(0, default);

        var dto = Assert.Single(result);
        Assert.Equal("vm.created", dto.Action);
        Assert.Equal("vm", dto.ResourceType);
        Assert.Equal("api-1", dto.ResourceName);
    }

    [Fact]
    public async Task ListAsync_NeverQueriesAnotherTenant()
    {
        _currentUser.Setup(u => u.TenantId).Returns(TenantId);
        _repo.Setup(r => r.ListByTenantAsync(It.IsAny<Guid>(), It.IsAny<int>(), default))
            .ReturnsAsync(new List<ActivityEvent>());

        await Create().ListAsync(20, default);

        _repo.Verify(r => r.ListByTenantAsync(TenantId, It.IsAny<int>(), default), Times.Once);
        _repo.Verify(r => r.ListByTenantAsync(It.Is<Guid>(g => g != TenantId), It.IsAny<int>(), default), Times.Never);
    }

    [Theory]
    [InlineData(0, ActivityService.DefaultLimit)]     // missing → default
    [InlineData(-5, ActivityService.DefaultLimit)]    // invalid → default
    [InlineData(5, 5)]                                // valid → passthrough
    [InlineData(1000, ActivityService.MaxLimit)]      // over cap → clamped
    public async Task ListAsync_ClampsLimit(int requested, int expected)
    {
        _currentUser.Setup(u => u.TenantId).Returns(TenantId);
        _repo.Setup(r => r.ListByTenantAsync(TenantId, It.IsAny<int>(), default))
            .ReturnsAsync(new List<ActivityEvent>());

        await Create().ListAsync(requested, default);

        _repo.Verify(r => r.ListByTenantAsync(TenantId, expected, default), Times.Once);
    }

    // A caller belonging to no tenant is rejected, not served an empty list.
    [Fact]
    public async Task ListAsync_NoTenantContext_Throws()
    {
        _currentUser.Setup(u => u.TenantId).Returns((Guid?)null);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() => Create().ListAsync(20, default));
        Assert.Equal("NOT_A_MEMBER", ex.Code);
    }
}
