using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.SshKey;
using Titan.Application.Commands.Compute.SshKey.Contracts;
using Titan.Application.Queries.Compute.SshKey;
using Titan.Application.Queries.Compute.SshKey.Contracts;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Tests.Domain;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

public class SshKeyServiceTests
{
    private readonly FakeSshKeyRepository _keys = new();
    private readonly FakeActivityRecorder _activity = new();
    private readonly Mock<ITenantRepository> _tenants = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Tenant _tenant = TestTenant.Active("acme", "kc-1", "a@acme.com");

    public SshKeyServiceTests()
    {
        _tenants.Setup(r => r.FindByIdAsync(_tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_tenant);
        _currentUser.SetupGet(u => u.TenantId).Returns(_tenant.Id);
        _currentUser.SetupGet(u => u.KeycloakUserId).Returns("kc-1");
    }

    private Task<AddSshKeyCommandResult> AddAsync(string name, string publicKey) =>
        new AddSshKeyCommandHandler(_keys, _tenants.Object, _currentUser.Object, _activity)
            .HandleAsync(new AddSshKeyCommand(name, publicKey));

    private Task DeleteAsync(Guid id) =>
        new DeleteSshKeyCommandHandler(_keys, _currentUser.Object, _activity).HandleAsync(new DeleteSshKeyCommand(id));

    private Task<List<SshKeyQueryResult>> ListAsync() =>
        new ListSshKeysQueryHandler(_keys, _currentUser.Object).HandleAsync(new ListSshKeysQuery());

    [Fact]
    public async Task Add_ThenList_ShowsTheKeyWithItsFingerprint()
    {
        AddSshKeyCommandResult added = await AddAsync("laptop", SshPublicKeyTests.Ed25519);

        SshKeyQueryResult listed = Assert.Single(await ListAsync());
        Assert.Equal(added.Id, listed.Id);
        Assert.Equal("laptop", listed.Name);
        Assert.Equal("ssh-ed25519", listed.Type);
        Assert.Equal(SshPublicKeyTests.Ed25519Fingerprint, listed.Fingerprint);
    }

    [Fact]
    public async Task Add_RecordsTheRequestAndTheOutcome_InTheSameSave()
    {
        AddSshKeyCommandResult added = await AddAsync("laptop", SshPublicKeyTests.Ed25519);

        Assert.Equal(["ssh_key.create_requested", "ssh_key.created"], _activity.Events.Select(e => e.Action));
        Assert.All(_activity.Events, e => Assert.Equal(added.Id, e.ResourceId));
        Assert.Equal(1, _keys.SaveCount);
    }

    [Fact]
    public async Task Add_SameKeyWithAnotherComment_IsSshKeyExists()
    {
        await AddAsync("laptop", SshPublicKeyTests.Ed25519);

        ConflictException refused = await Assert.ThrowsAsync<ConflictException>(
            () => AddAsync("desktop", SshPublicKeyTests.Ed25519.Replace("alice@laptop", "other")));
        Assert.Equal("SSH_KEY_EXISTS", refused.Code);
        Assert.Single(_keys.Saved);
    }

    [Fact]
    public async Task Add_SameName_IsSshKeyNameTaken()
    {
        await AddAsync("laptop", SshPublicKeyTests.Ed25519);

        ConflictException refused = await Assert.ThrowsAsync<ConflictException>(
            () => AddAsync(" laptop ", SshPublicKeyTests.Rsa2048));
        Assert.Equal("SSH_KEY_NAME_TAKEN", refused.Code);
    }

    [Fact]
    public async Task Add_AtTheTenantCap_IsRefused_AndSavesNothing()
    {
        _tenant.UpdateQuota(TenantQuota.Create(10, 1, 10, 3, 3, 20, 10, 1, 5));
        await AddAsync("laptop", SshPublicKeyTests.Ed25519);

        BusinessException refused = await Assert.ThrowsAsync<BusinessException>(
            () => AddAsync("desktop", SshPublicKeyTests.Rsa2048));
        Assert.Equal("SSH_KEY_QUOTA_EXCEEDED", refused.Code);
        Assert.Single(_keys.Saved);
    }

    [Fact]
    public async Task List_ShowsOnlyTheCallersTenant()
    {
        await AddAsync("laptop", SshPublicKeyTests.Ed25519);
        Tenant other = TestTenant.Active("other", "kc-2", "b@other.com");
        _keys.Saved.Add(Titan.Domain.Aggregates.SshKey.SshKey.Create(other, 0, "theirs", SshPublicKeyTests.Rsa2048));

        SshKeyQueryResult listed = Assert.Single(await ListAsync());
        Assert.Equal("laptop", listed.Name);
    }

    [Fact]
    public async Task Delete_RemovesTheKey_AndRecordsIt()
    {
        AddSshKeyCommandResult added = await AddAsync("laptop", SshPublicKeyTests.Ed25519);
        _activity.Events.Clear();

        await DeleteAsync(added.Id);

        Assert.Empty(await ListAsync());
        Assert.Equal(["ssh_key.delete_requested", "ssh_key.deleted"], _activity.Events.Select(e => e.Action));
    }

    [Fact]
    public async Task Delete_AnotherTenantsKey_IsNotFound_AndKeepsIt()
    {
        Tenant other = TestTenant.Active("other", "kc-2", "b@other.com");
        var theirs = Titan.Domain.Aggregates.SshKey.SshKey.Create(other, 0, "theirs", SshPublicKeyTests.Rsa2048);
        _keys.Saved.Add(theirs);

        NotFoundException refused = await Assert.ThrowsAsync<NotFoundException>(() => DeleteAsync(theirs.Id));
        Assert.Equal("SSH_KEY_NOT_FOUND", refused.Code);
        Assert.Single(_keys.Saved);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task Delete_UnknownId_IsNotFound()
    {
        NotFoundException refused = await Assert.ThrowsAsync<NotFoundException>(() => DeleteAsync(Guid.NewGuid()));
        Assert.Equal("SSH_KEY_NOT_FOUND", refused.Code);
    }
}
