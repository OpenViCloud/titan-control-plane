using FastEndpoints;
using FluentValidation;
using Titan.Application.Commands.Compute.Vm.Contracts;

using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;

namespace Titan.Infrastructure.Endpoints.Vm;

// The rule is the Domain's (titan-ops architecture/code-structure.md#logic-placement); this answers 400 in the API's words.
public class CreateVmValidator : Validator<CreateVmCommand>
{
    private static readonly System.Text.RegularExpressions.Regex NameRegex =
        new(VmAggregate.NamePattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    public CreateVmValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(VmAggregate.NameMaxLength)
            .Matches(NameRegex).WithMessage("'Name' may only contain letters, digits, and hyphens, and must start with a letter or digit.");
        RuleFor(x => x.NetworkId).NotEmpty();
        RuleFor(x => x.VmOfferingId).NotEmpty();
        RuleFor(x => x.TemplateId).NotEmpty();
        // Absent means the network's cluster (titan-ops features/catalog/design.md#cluster-is-a-real-choice); sent, it has a value.
        RuleFor(x => x.ClusterId).NotEmpty().When(x => x.ClusterId != null);
        RuleFor(x => x.DiskSizeGb).GreaterThan(0).When(x => x.DiskSizeGb.HasValue);
        When(x => x.UserData != null, () =>
            RuleFor(x => x.UserData)
                .Must(s => System.Text.Encoding.UTF8.GetByteCount(s!) <= 32_768)
                .WithMessage("'UserData' must not exceed 32768 bytes (UTF-8 encoded)."));
        // 12 to 72 characters (titan-ops features/vm/design.md#console-password), in the OpenAPI schema as minLength and
        // maxLength; absent is no password, and the message never repeats the value.
        RuleFor(x => x.ConsolePassword)
            .Length(ConsolePasswordMinLength, ConsolePasswordMaxLength)
            .WithMessage($"'ConsolePassword' must be {ConsolePasswordMinLength} to {ConsolePasswordMaxLength} characters.");
        // A predicate, not a comparison rule on the count: that one lands in the OpenAPI schema as `maximum` on an array.
        RuleFor(x => x.SshKeyIds)
            .Must(ids => ids == null || ids.Distinct().Count() <= MaxSshKeysPerVm)
            .WithMessage($"Pick at most {MaxSshKeysPerVm} SSH keys.");
    }

    public const int MaxSshKeysPerVm = 10;
    public const int ConsolePasswordMinLength = 12;
    public const int ConsolePasswordMaxLength = 72;
}
