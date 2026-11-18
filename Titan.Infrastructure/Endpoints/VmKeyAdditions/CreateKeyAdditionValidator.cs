using FastEndpoints;
using FluentValidation;

namespace Titan.Infrastructure.Endpoints.VmKeyAdditions;

// 1 to 10 distinct ids (titan-ops features/vm/design.md#ssh-key); duplicates merge at the handler, as a VM's create
// does (CreateVmValidator.SshKeyIds), so a predicate on the count, not a comparison FluentValidation would turn into
// an OpenAPI `maximum` on the array.
public class CreateKeyAdditionValidator : Validator<CreateKeyAdditionBody>
{
    public const int MaxSshKeysPerKeyAddition = 10;

    public CreateKeyAdditionValidator()
    {
        RuleFor(x => x.SshKeyIds)
            .Must(ids => ids != null && ids.Distinct().Count() is >= 1 and <= MaxSshKeysPerKeyAddition)
            .WithMessage($"Pick 1 to {MaxSshKeysPerKeyAddition} SSH keys.");
    }
}
