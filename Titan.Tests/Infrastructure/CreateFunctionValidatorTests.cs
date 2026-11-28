using FluentValidation.TestHelper;
using Titan.Application.Commands.Compute.Function.Contracts;
using Titan.Infrastructure.Endpoints.Function;

namespace Titan.Tests.Infrastructure;

// A function's name follows the load balancer's rule (titan-ops architecture/domain-methods.md#names-and-sizes).
public class CreateFunctionValidatorTests
{
    private readonly CreateFunctionValidator _validator = new();

    [Theory]
    [InlineData("hello", true)]
    [InlineData("a_b-1", true)]
    [InlineData("-hello", false)]
    [InlineData("hello world", false)]
    [InlineData("", false)]
    public void TheName_IsLettersDigitsHyphensUnderscores_StartingWithALetterOrDigit(string name, bool valid)
    {
        var result = _validator.TestValidate(new CreateFunctionCommand(name, "fn-dev"));

        if (valid) result.ShouldNotHaveValidationErrorFor(c => c.Name);
        else result.ShouldHaveValidationErrorFor(c => c.Name);
    }

    [Fact]
    public void AName_Of100Characters_IsAccepted_And101_IsRejected()
    {
        _validator.TestValidate(new CreateFunctionCommand(new string('a', 100), "fn-dev")).ShouldNotHaveValidationErrorFor(c => c.Name);
        _validator.TestValidate(new CreateFunctionCommand(new string('a', 101), "fn-dev")).ShouldHaveValidationErrorFor(c => c.Name);
    }

    [Fact]
    public void TheCluster_IsRequired()
    {
        _validator.TestValidate(new CreateFunctionCommand("hello", "")).ShouldHaveValidationErrorFor(c => c.ClusterId);
    }
}
