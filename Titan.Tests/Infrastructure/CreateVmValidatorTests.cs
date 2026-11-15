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

    // A console password is 12 to 72 characters (titan-ops features/vm/design.md#console-password); absent is no password.
    [Theory]
    [InlineData(12)]
    [InlineData(72)]
    public void AConsolePassword_Of12To72Characters_IsAccepted(int length)
    {
        CreateVmCommand request = Request(null) with { ConsolePassword = new string('p', length) };
        _validator.TestValidate(request).ShouldNotHaveValidationErrorFor(r => r.ConsolePassword);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(73)]
    public void AConsolePassword_OutsideTwelveToSeventyTwo_IsRejected_WithoutEchoingIt(int length)
    {
        string password = new string('Q', length);
        var result = _validator.TestValidate(Request(null) with { ConsolePassword = password });
        result.ShouldHaveValidationErrorFor(r => r.ConsolePassword);
        // The 400 body carries each error's message (Program.cs), so no message may carry the password.
        if (length > 0)
        {
            Assert.All(result.Errors, error => Assert.DoesNotContain(password, error.ErrorMessage));
        }
    }

    // A record prints every property; a log of the command must not carry the password.
    [Fact]
    public void TheCommandsText_HidesTheConsolePassword()
    {
        string text = (Request(null) with { ConsolePassword = "Secret-Pass-123" }).ToString();
        Assert.DoesNotContain("Secret-Pass-123", text);
        Assert.Contains("ConsolePassword = ***", text);
    }

    [Fact]
    public void NoConsolePassword_IsAccepted()
    {
        _validator.TestValidate(Request(null)).ShouldNotHaveValidationErrorFor(r => r.ConsolePassword);
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
