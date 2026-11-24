using System.Text.RegularExpressions;
using FastEndpoints;
using FluentValidation;
using Titan.Application.Services.Platform.Backup;

using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;

namespace Titan.Infrastructure.Endpoints.Admin;

// The name rule is the Domain's, the one a VM's create answers with (titan-ops architecture/code-structure.md#logic-placement);
// the key is held to the shape of an archive's key before anything is read.
public class RestoreVmFromBackupValidator : Validator<RestoreVmFromBackupBody>
{
    private static readonly Regex BackupKeyRegex = new(IncusBackupKey.BackupKeyPattern, RegexOptions.Compiled);
    private static readonly Regex NameRegex = new(VmAggregate.NamePattern, RegexOptions.Compiled);

    public RestoreVmFromBackupValidator()
    {
        RuleFor(x => x.BackupKey).NotEmpty()
            .Matches(BackupKeyRegex).WithMessage("'BackupKey' must be the key of a backup, like 20261120T010000Z.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(VmAggregate.NameMaxLength)
            .Matches(NameRegex).WithMessage("'Name' may only contain letters, digits, and hyphens, and must start with a letter or digit.");
    }
}
