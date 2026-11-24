using System.Text.RegularExpressions;
using FastEndpoints;
using FluentValidation;
using Titan.Application.Services.Platform.Backup;

using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;

namespace Titan.Infrastructure.Endpoints.Admin;

// The name rule is the Domain's, the one a volume's create answers with (titan-ops architecture/code-structure.md#logic-placement);
// the key is held to the shape of an archive's key before anything is read.
public class RestoreVolumeFromBackupValidator : Validator<RestoreVolumeFromBackupBody>
{
    private static readonly Regex BackupKeyRegex = new(IncusBackupKey.BackupKeyPattern, RegexOptions.Compiled);

    public RestoreVolumeFromBackupValidator()
    {
        RuleFor(x => x.BackupKey).NotEmpty()
            .Matches(BackupKeyRegex).WithMessage("'BackupKey' must be the key of a backup, like 20261120T010000Z.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(VolumeAggregate.NameMaxLength);
    }
}
