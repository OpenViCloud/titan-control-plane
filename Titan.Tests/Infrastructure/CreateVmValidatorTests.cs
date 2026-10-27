using FluentValidation.TestHelper;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Infrastructure.Endpoints.Vm;

namespace Titan.Tests.Infrastructure;

public class CreateVmValidatorTests
{
    private readonly CreateVmValidator _validator = new();

    private static CreateVmCommand Request(List<Guid>? sshKeyIds) =>
        new("web-01", Guid.NewGuid(), "m", Guid.NewGuid().ToString(), "cluster-1", null, SshKeyIds: sshKeyIds);

    private static List<Guid> Ids(int count) => Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();

    [Fact]
    public void ElevenSshKeys_AreRejected()
    {
        _validator.TestValidate(Request(Ids(11))).ShouldHaveValidationErrorFor(r => r.SshKeyIds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void UpToTenSshKeys_AreAccepted(int count)
    {
        _validator.TestValidate(Request(Ids(count))).ShouldNotHaveValidationErrorFor(r => r.SshKeyIds);
    }

    // The handler puts a repeated id in once, so the cap counts distinct ids.
    [Fact]
    public void ElevenCopiesOfOneId_AreAccepted()
    {
        Guid id = Guid.NewGuid();
        _validator.TestValidate(Request(Enumerable.Repeat(id, 11).ToList())).ShouldNotHaveValidationErrorFor(r => r.SshKeyIds);
    }

    [Fact]
    public void NoSshKeyList_IsAccepted()
    {
        _validator.TestValidate(Request(null)).ShouldNotHaveValidationErrorFor(r => r.SshKeyIds);
    }
}
