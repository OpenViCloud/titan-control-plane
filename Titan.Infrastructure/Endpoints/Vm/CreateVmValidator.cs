using FastEndpoints;
using FluentValidation;
using Titan.Application.Commands.Compute.Vm.Contracts;

namespace Titan.Infrastructure.Endpoints.Vm;

public class CreateVmValidator : Validator<CreateVmCommand>
{
    private static readonly System.Text.RegularExpressions.Regex NameRegex =
        new(@"^[a-zA-Z0-9][a-zA-Z0-9-]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public CreateVmValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(64)
            .Matches(NameRegex).WithMessage("'Name' may only contain letters, digits, and hyphens, and must start with a letter or digit.");
        RuleFor(x => x.NetworkId).NotEmpty();
        RuleFor(x => x.VmOfferingId).NotEmpty();
        RuleFor(x => x.TemplateId).NotEmpty();
        RuleFor(x => x.ClusterId).NotEmpty();
        RuleFor(x => x.DiskSizeGb).GreaterThan(0).When(x => x.DiskSizeGb.HasValue);
        When(x => x.UserData != null, () =>
            RuleFor(x => x.UserData)
                .Must(s => System.Text.Encoding.UTF8.GetByteCount(s!) <= 32_768)
                .WithMessage("'UserData' must not exceed 32768 bytes (UTF-8 encoded)."));
        // A predicate, not a comparison rule on the count: that one lands in the OpenAPI schema as `maximum` on an array.
        RuleFor(x => x.SshKeyIds)
            .Must(ids => ids == null || ids.Distinct().Count() <= MaxSshKeysPerVm)
            .WithMessage($"Pick at most {MaxSshKeysPerVm} SSH keys.");
    }

    public const int MaxSshKeysPerVm = 10;
}
