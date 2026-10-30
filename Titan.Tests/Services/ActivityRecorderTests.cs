using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.ActivityEvent;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Tests.Services;

public class ActivityRecorderTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly CapturingRepo _repo = new();

    public ActivityRecorderTests()
    {
        _currentUser.Setup(u => u.TenantId).Returns(TenantId);
        _currentUser.Setup(u => u.KeycloakUserId).Returns("kc-user-9");
    }

    private ActivityRecorder Create() => new(_repo, _currentUser.Object);

    // The recorder resolves tenant + actor from the request context, so call sites say only WHAT.
    [Fact]
    public void Stage_BuildsEventFromCurrentUser_DoesNotSave()
    {
        var id = Guid.NewGuid();

        Create().Stage("vm.created", "vm", id, "web-01");

        var e = Assert.Single(_repo.Added);
        Assert.Equal(TenantId, e.TenantId);
        Assert.Equal("vm.created", e.Action);
        Assert.Equal("vm", e.ResourceType);
        Assert.Equal(id, e.ResourceId);
        Assert.Equal("web-01", e.ResourceName);
        Assert.Equal("kc-user-9", e.ActorKeycloakUserId);
        Assert.Equal(0, _repo.SaveCount); // Stage leaves the flush to the caller's SaveChanges
    }

    [Fact]
    public async Task RecordAsync_StagesAndSaves()
    {
        await Create().RecordAsync("vm.stopped", "vm", Guid.NewGuid(), "web-01");

        Assert.Single(_repo.Added);
        Assert.Equal(1, _repo.SaveCount);
    }

    // These are always member-initiated writes, so a missing tenant is a programming error, not a
    // silent no-op — surface it (RequireTenantId → 403 NOT_A_MEMBER).
    [Fact]
    public void Stage_NoTenantContext_Throws()
    {
        _currentUser.Setup(u => u.TenantId).Returns((Guid?)null);

        Assert.Throws<ForbiddenException>(() => Create().Stage("vm.created", "vm", Guid.NewGuid(), "x"));
    }

    // titan-ops features/activity/design.md#request-and-outcome-names — an outcome is observed by an internal callback that acts for no tenant and
    // no user. The tenant comes from the resource; the actor is "system". The request-side rule above
    // (throw without a tenant) must NOT reach this path, or every outcome would 403.
    [Fact]
    public void StageOutcome_UsesTheGivenTenant_AndNeedsNoCurrentUser()
    {
        _currentUser.Setup(u => u.TenantId).Returns((Guid?)null);
        // Whoever the caller is, the outcome is not theirs: it must not borrow the caller's identity.
        _currentUser.Setup(u => u.KeycloakUserId).Returns("kc-some-caller");
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();

        Create().StageOutcome(owner, "vm.deleted", "vm", id, "web-01");

        var e = Assert.Single(_repo.Added);
        Assert.Equal(owner, e.TenantId);
        Assert.Equal("vm.deleted", e.Action);
        Assert.Equal(id, e.ResourceId);
        Assert.Equal("system", e.ActorKeycloakUserId);
        Assert.Equal(0, _repo.SaveCount); // flushed by the callback's own SaveChanges, with the status
    }

    private class CapturingRepo : IActivityEventRepository
    {
        public List<ActivityEvent> Added { get; } = new();
        public int SaveCount { get; private set; }
        public void Add(ActivityEvent e) => Added.Add(e);
        public Task SaveAsync(CancellationToken ct = default) { SaveCount++; return Task.CompletedTask; }
        public Task<List<ActivityEvent>> ListByTenantAsync(Guid t, int l, CancellationToken ct = default) =>
            Task.FromResult(new List<ActivityEvent>());
    }
}
