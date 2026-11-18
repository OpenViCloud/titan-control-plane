using FluentValidation.TestHelper;
using Titan.Infrastructure.Endpoints.VmKeyAdditions;

namespace Titan.Tests.Infrastructure;

// titan-ops features/vm/design.md#ssh-key: 1 to 10 distinct ids, duplicates merged, the same rule the domain's SshKey
// quota has nothing to do with — this is just the shape of the request body.
public class CreateKeyAdditionValidatorTests
{
    private readonly CreateKeyAdditionValidator _validator = new();

    [Fact]
    public void OneId_Passes()
    {
        _validator.TestValidate(new CreateKeyAdditionBody([Guid.NewGuid()])).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void TenDistinctIds_Pass()
    {
        var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();

        _validator.TestValidate(new CreateKeyAdditionBody(ids)).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void ElevenDistinctIds_AreRejected()
    {
        var ids = Enumerable.Range(0, 11).Select(_ => Guid.NewGuid()).ToList();

        _validator.TestValidate(new CreateKeyAdditionBody(ids)).ShouldHaveValidationErrorFor(x => x.SshKeyIds);
    }

    // Duplicates merge at the handler, so eleven copies of one id are still one key: the OpenAPI schema carries no
    // `maximum` on the array for exactly this reason (CreateVmValidatorTests.ElevenCopiesOfOneId_AreAccepted).
    [Fact]
    public void ElevenCopiesOfOneId_AreAccepted()
    {
        var id = Guid.NewGuid();
        var ids = Enumerable.Repeat(id, 11).ToList();

        _validator.TestValidate(new CreateKeyAdditionBody(ids)).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void EmptyList_IsRejected()
    {
        _validator.TestValidate(new CreateKeyAdditionBody([])).ShouldHaveValidationErrorFor(x => x.SshKeyIds);
    }
}
