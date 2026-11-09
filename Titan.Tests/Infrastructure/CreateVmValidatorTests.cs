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

    // The name rule is Vm's (Vm.NameMaxLength, Vm.NamePattern); the validator answers 400 from the same constants.
    [Fact]
    public void AName_Of64Characters_IsAccepted_And65_IsRejected()
    {
        CreateVmCommand Named(string name) => Request(null) with { Name = name };
        _validator.TestValidate(Named(new string('a', 64))).ShouldNotHaveValidationErrorFor(r => r.Name);
        _validator.TestValidate(Named(new string('a', 65))).ShouldHaveValidationErrorFor(r => r.Name);
        _validator.TestValidate(Named("a_b")).ShouldHaveValidationErrorFor(r => r.Name);
    }

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

    // Absent means the network's cluster (titan-ops features/catalog/design.md#cluster-is-a-real-choice); an empty string
    // is a value, and a wrong one.
    [Fact]
    public void AnEmptyClusterId_IsRejected_AMissingOne_IsAccepted()
    {
        _validator.TestValidate(Request(null) with { ClusterId = "" }).ShouldHaveValidationErrorFor(r => r.ClusterId);
        _validator.TestValidate(Request(null) with { ClusterId = null }).ShouldNotHaveValidationErrorFor(r => r.ClusterId);
        _validator.TestValidate(Request(null)).ShouldNotHaveValidationErrorFor(r => r.ClusterId);
    }

    [Fact]
    public void NoSshKeyList_IsAccepted()
    {
        _validator.TestValidate(Request(null)).ShouldNotHaveValidationErrorFor(r => r.SshKeyIds);
    }
}
