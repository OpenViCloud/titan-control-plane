using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmKeyAddition.Contracts;
using Titan.Application.Queries.Compute.VmKeyAddition.Contracts;
using VmKeyAdditionCmd = Titan.Application.Commands.Compute.VmKeyAddition;
using VmKeyAdditionQry = Titan.Application.Queries.Compute.VmKeyAddition;
using Titan.Domain.Aggregates.SshKey;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Tests.Domain;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// A key addition is written through its VM (titan-ops architecture/code-structure.md#child-entity): the create and the
// callback load the VM with its row locked, change it and save it through IVmRepository. IVmKeyAdditionRepository is
// read side only, so only the list and get queries ask it.
public class VmKeyAdditionServiceTests
{
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant TenantIdOwner = TestTenant.WithRoomToSpare();
    private static readonly Guid TenantId = TenantIdOwner.Id;
    private static string Project => "t-" + TenantId.ToString("N");

    private readonly Mock<IVmRepository> _vmRepo = new();
    private readonly Mock<IVmKeyAdditionRepository> _additionRepo = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IProviderCommandQueue> _commands = new();
    private readonly Mock<IActivityRecorder> _activity = new();
    private readonly FakeSshKeyRepository _sshKeys = new();

    private void SetupCurrentUser(Guid tenantId) => _currentUser.Setup(u => u.TenantId).Returns(tenantId);

    private Vm Given(Vm vm)
    {
        _vmRepo.Setup(r => r.FindByIdAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);
        _vmRepo.Setup(r => r.FindByIdForUpdateAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);
        return vm;
    }

    private static Vm MakeRunningVm()
    {
        var vm = Vm.Create(TenantIdOwner.Id, Guid.NewGuid(), "web-01", "offering-1", "tmpl-1", "cluster-1", 0);
        vm.MarkRunning("cs-vm-id", "10.0.0.1");
        return vm;
    }

    private VmKeyAdditionCmd.CreateVmKeyAdditionCommandHandler CreateHandler() =>
        new(_vmRepo.Object, _sshKeys, _currentUser.Object, _commands.Object, _activity.Object);

    private VmKeyAdditionCmd.EndVmKeyAdditionRequestCommandHandler EndHandler() =>
        new(_vmRepo.Object, _activity.Object);

    private VmKeyAdditionQry.ListVmKeyAdditionsQueryHandler ListHandler() =>
        new(_vmRepo.Object, _additionRepo.Object, _currentUser.Object);

    private VmKeyAdditionQry.GetVmKeyAdditionQueryHandler GetHandler() =>
        new(_vmRepo.Object, _additionRepo.Object, _currentUser.Object);

    private SshKey AKey(string name = "laptop") => SshKey.Create(TenantId, name, SshPublicKeyTests.Ed25519);

    // ── Create success ───────────────────────────────────────

    [Fact]
    public async Task Create_Success_ReturnsTheId_AddsTheAddition_AndEnqueuesTheCommandWithRebuiltKeyLines()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var key = AKey();
        _sshKeys.Saved.Add(key);

        var result = await CreateHandler().HandleAsync(new CreateVmKeyAdditionCommand(vm.Id, [key.Id]), default);

        Assert.NotEqual(Guid.Empty, result.KeyAdditionId);
        VmKeyAddition addition = vm.KeyAdditions.Single(a => a.Id == result.KeyAdditionId);
        Assert.Equal(VmKeyAdditionStatus.Adding, addition.Status);
        Assert.Equal([key.Id], addition.SshKeyIds);
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueVmKeyAdditionCreateAsync(
            It.Is<Vm>(a => a.Id == vm.Id), It.Is<VmKeyAddition>(a => a.Id == result.KeyAdditionId),
            addition.PendingId!.Value, "cs-vm-id", Project,
            It.Is<List<string>>(lines => lines.Count == 1 && lines[0] == key.PublicKey),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_LoadsTheVmForUpdate_NotTheUnlockedRead()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdForUpdateAsync(vm.Id, default)).ReturnsAsync(vm);
        var key = AKey();
        _sshKeys.Saved.Add(key);

        var result = await CreateHandler().HandleAsync(new CreateVmKeyAdditionCommand(vm.Id, [key.Id]), default);

        _ = result;
        _vmRepo.Verify(r => r.FindByIdForUpdateAsync(vm.Id, default), Times.Once);
        _vmRepo.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_DuplicateIds_AreMergedIntoOne()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var key = AKey();
        _sshKeys.Saved.Add(key);

        var result = await CreateHandler().HandleAsync(new CreateVmKeyAdditionCommand(vm.Id, [key.Id, key.Id]), default);

        VmKeyAddition addition = vm.KeyAdditions.Single(a => a.Id == result.KeyAdditionId);
        Assert.Equal([key.Id], addition.SshKeyIds);
    }

    [Fact]
    public async Task Create_VmNotRunning_ThrowsBusinessException_AndEnqueuesNothing()
    {
        SetupCurrentUser(TenantId);
        var vm = Vm.Create(TenantIdOwner.Id, Guid.NewGuid(), "web-01", "offering-1", "tmpl-1", "cluster-1", 0);
        Given(vm);
        var key = AKey();
        _sshKeys.Saved.Add(key);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateHandler().HandleAsync(new CreateVmKeyAdditionCommand(vm.Id, [key.Id]), default));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        Assert.Empty(vm.KeyAdditions);
        _commands.Verify(w => w.EnqueueVmKeyAdditionCreateAsync(
            It.IsAny<Vm>(), It.IsAny<VmKeyAddition>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Two additions waiting on one VM could lose a key in the worker's pull-append-push (the command table may run
    // two commands at once): the second is refused with no row and no command.
    [Fact]
    public async Task Create_WhileAnotherAddition_IsStillAdding_IsRefused_AddsNothing_AndEnqueuesNothing()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var key = AKey();
        _sshKeys.Saved.Add(key);
        await CreateHandler().HandleAsync(new CreateVmKeyAdditionCommand(vm.Id, [key.Id]), default);
        _commands.Invocations.Clear();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateHandler().HandleAsync(new CreateVmKeyAdditionCommand(vm.Id, [key.Id]), default));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        Assert.Single(vm.KeyAdditions);
        _commands.Verify(w => w.EnqueueVmKeyAdditionCreateAsync(
            It.IsAny<Vm>(), It.IsAny<VmKeyAddition>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Once the first ends, Added or Failed, a new request is accepted.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Create_AfterTheFirstAdditionEnds_IsAccepted(bool firstSucceeded)
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var key = AKey();
        _sshKeys.Saved.Add(key);
        var first = await CreateHandler().HandleAsync(new CreateVmKeyAdditionCommand(vm.Id, [key.Id]), default);
        VmKeyAddition firstAddition = vm.KeyAdditions.Single();
        if (firstSucceeded)
        {
            vm.KeyAdditionSucceeded(first.KeyAdditionId, firstAddition.PendingId!.Value, false);
        }
        else
        {
            vm.KeyAdditionFailed(first.KeyAdditionId, firstAddition.PendingId!.Value, "r", "d");
        }

        var second = await CreateHandler().HandleAsync(new CreateVmKeyAdditionCommand(vm.Id, [key.Id]), default);

        Assert.Equal(2, vm.KeyAdditions.Count);
        Assert.Equal(VmKeyAdditionStatus.Adding, vm.KeyAdditions.Single(a => a.Id == second.KeyAdditionId).Status);
    }

    [Fact]
    public async Task Create_WithAnUnknownKeyId_IsSshKeyNotFound_AndAddsNothing()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateHandler().HandleAsync(new CreateVmKeyAdditionCommand(vm.Id, [Guid.NewGuid()]), default));

        Assert.Equal("SSH_KEY_NOT_FOUND", ex.Code);
        Assert.Empty(vm.KeyAdditions);
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task Create_WithAnotherTenantsKey_IsSshKeyNotFound()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var foreignKey = SshKey.Create(Guid.NewGuid(), "someone-elses", SshPublicKeyTests.Ed25519);
        _sshKeys.Saved.Add(foreignKey);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateHandler().HandleAsync(new CreateVmKeyAdditionCommand(vm.Id, [foreignKey.Id]), default));

        Assert.Equal("SSH_KEY_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Create_RecordsTheRequestActivityOnce()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var key = AKey();
        _sshKeys.Saved.Add(key);

        await CreateHandler().HandleAsync(new CreateVmKeyAdditionCommand(vm.Id, [key.Id]), default);

        _activity.Verify(a => a.Stage("vm.add_keys_requested", "vm", vm.Id, vm.Name), Times.Once);
    }

    // ── End request: Done ──────────────────────────────────────

    [Fact]
    public async Task EndRequest_Done_EndsTheAddition_RecordsRestartNeeded_AndTheOutcomeOnce()
    {
        var vm = Given(MakeRunningVm());
        vm.AddKeys(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()]);
        VmKeyAddition addition = vm.KeyAdditions.Single();
        Guid requestId = addition.PendingId!.Value;

        await EndHandler().HandleAsync(new EndVmKeyAdditionRequestCommand(
            vm.Id, addition.Id, requestId, "Done", RestartNeeded: true), default);

        Assert.Equal(VmKeyAdditionStatus.Added, addition.Status);
        Assert.True(addition.RestartNeeded);
        _activity.Verify(a => a.StageOutcome(vm.TenantId, "vm.keys_added", "vm", vm.Id, vm.Name), Times.Once);
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task EndRequest_Done_WithoutRestartNeeded_DefaultsToFalse()
    {
        var vm = Given(MakeRunningVm());
        vm.AddKeys(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()]);
        VmKeyAddition addition = vm.KeyAdditions.Single();

        await EndHandler().HandleAsync(new EndVmKeyAdditionRequestCommand(
            vm.Id, addition.Id, addition.PendingId!.Value, "Done"), default);

        Assert.False(addition.RestartNeeded);
    }

    // ── End request: Failed ─────────────────────────────────────

    [Fact]
    public async Task EndRequest_Failed_RecordsTheReason_AndTheFailedOutcomeOnce()
    {
        var vm = Given(MakeRunningVm());
        vm.AddKeys(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()]);
        VmKeyAddition addition = vm.KeyAdditions.Single();

        await EndHandler().HandleAsync(new EndVmKeyAdditionRequestCommand(
            vm.Id, addition.Id, addition.PendingId!.Value, "Failed", Reason: "AGENT_NOT_READY", Detail: "raw"), default);

        Assert.Equal(VmKeyAdditionStatus.Failed, addition.Status);
        Assert.Equal("AGENT_NOT_READY", addition.FailureReason);
        _activity.Verify(a => a.StageOutcome(vm.TenantId, "vm.add_keys_failed", "vm", vm.Id, vm.Name), Times.Once);
    }

    // ── A report that names no waiting request is ignored ──────

    [Fact]
    public async Task EndRequest_WithAForeignRequestId_Is204_RecordsNoActivity_AndSavesNothing()
    {
        var vm = Given(MakeRunningVm());
        vm.AddKeys(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()]);
        VmKeyAddition addition = vm.KeyAdditions.Single();

        await EndHandler().HandleAsync(new EndVmKeyAdditionRequestCommand(
            vm.Id, addition.Id, Guid.NewGuid(), "Done", RestartNeeded: true), default);

        Assert.Equal(VmKeyAdditionStatus.Adding, addition.Status);
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Never);
    }

    [Fact]
    public async Task EndRequest_OfAnUnknownKeyAddition_IsNotFound()
    {
        var vm = Given(MakeRunningVm());

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => EndHandler().HandleAsync(
            new EndVmKeyAdditionRequestCommand(vm.Id, Guid.NewGuid(), Guid.NewGuid(), "Done"), default));

        Assert.Equal("VM_KEY_ADDITION_NOT_FOUND", ex.Code);
    }

    // ── Queries ──────────────────────────────────────────────

    [Fact]
    public async Task List_ReturnsTheRepositorysRows_ForTheOwnedVm()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        vm.AddKeys(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()]);
        VmKeyAddition addition = vm.KeyAdditions.Single();
        _additionRepo.Setup(r => r.FindByVmIdAsync(vm.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([addition]);

        var result = await ListHandler().HandleAsync(new ListVmKeyAdditionsQuery(vm.Id), default);

        var row = Assert.Single(result);
        Assert.Equal(addition.Id, row.Id);
        Assert.Equal(VmKeyAdditionStatus.Adding, row.Status);
    }

    [Fact]
    public async Task List_OfAnotherTenantsVm_IsVmNotFound()
    {
        SetupCurrentUser(Guid.NewGuid());
        var vm = Given(MakeRunningVm());

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => ListHandler().HandleAsync(new ListVmKeyAdditionsQuery(vm.Id), default));

        Assert.Equal("VM_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Get_ReturnsTheOneRow()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        vm.AddKeys(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()]);
        VmKeyAddition addition = vm.KeyAdditions.Single();
        _additionRepo.Setup(r => r.FindByIdAsync(addition.Id, It.IsAny<CancellationToken>())).ReturnsAsync(addition);

        var result = await GetHandler().HandleAsync(new GetVmKeyAdditionQuery(vm.Id, addition.Id), default);

        Assert.Equal(addition.Id, result.Id);
    }

    [Fact]
    public async Task Get_OfAKeyAdditionOfAnotherVm_IsNotFound()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var otherVm = MakeRunningVm();
        otherVm.AddKeys(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()]);
        VmKeyAddition foreign = otherVm.KeyAdditions.Single();
        _additionRepo.Setup(r => r.FindByIdAsync(foreign.Id, It.IsAny<CancellationToken>())).ReturnsAsync(foreign);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => GetHandler().HandleAsync(new GetVmKeyAdditionQuery(vm.Id, foreign.Id), default));

        Assert.Equal("VM_KEY_ADDITION_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Get_OfAnUnknownId_IsNotFound()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => GetHandler().HandleAsync(new GetVmKeyAdditionQuery(vm.Id, Guid.NewGuid()), default));

        Assert.Equal("VM_KEY_ADDITION_NOT_FOUND", ex.Code);
    }
}
