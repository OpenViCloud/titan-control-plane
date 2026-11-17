using FluentValidation.TestHelper;
using Titan.Infrastructure.Endpoints.Vm;

namespace Titan.Tests.Infrastructure;

// PATCH /v1/tenant/vms/{vmId} takes a name, a stop timeout, or both, and at least one
// (titan-ops features/vm/design.md#vm-stop-timeout); each rule is Vm's, and the validator answers 400 from its constants.
public class UpdateVmValidatorTests
{
    private readonly UpdateVmValidator _validator = new();

    [Fact]
    public void ANameAlone_IsAccepted_AsARenameAlwaysWas()
    {
        _validator.TestValidate(new UpdateVmBody("web-02")).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void AStopTimeoutAlone_IsAccepted()
    {
        _validator.TestValidate(new UpdateVmBody(null, 600)).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Both_AreAccepted()
    {
        _validator.TestValidate(new UpdateVmBody("web-02", 1800)).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Neither_IsRejected()
    {
        Assert.False(_validator.Validate(new UpdateVmBody(null, null)).IsValid);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(1801)]
    public void AStopTimeoutOutside30To1800_IsRejected(int seconds)
    {
        _validator.TestValidate(new UpdateVmBody(null, seconds)).ShouldHaveValidationErrorFor(b => b.StopTimeoutSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a_b")]
    [InlineData("-a")]
    public void ANameOutsideTheRule_IsRejected(string name)
    {
        _validator.TestValidate(new UpdateVmBody(name)).ShouldHaveValidationErrorFor(b => b.Name);
    }

    [Fact]
    public void AName_Of64Characters_IsAccepted_And65_IsRejected()
    {
        _validator.TestValidate(new UpdateVmBody(new string('a', 64))).ShouldNotHaveValidationErrorFor(b => b.Name);
        _validator.TestValidate(new UpdateVmBody(new string('a', 65))).ShouldHaveValidationErrorFor(b => b.Name);
    }
}
