using FastEndpoints;
using FluentValidation;
using Titan.Application.Commands.Platform.BackupTarget;
using Titan.Application.Commands.Platform.BackupTarget.Contracts;
using BackupTargetAggregate = Titan.Domain.Aggregates.Platform.BackupTarget.BackupTarget;

namespace Titan.Infrastructure.Endpoints.Admin;

// One target for the environment, so no id in the route (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#backup-target).
// Every field arrives here; the secret may be absent once a target exists, which the validator cannot know.
public class SetBackupTargetEndpoint(SetBackupTargetCommandHandler handler) : Endpoint<SetBackupTargetCommand>
{
    public override void Configure()
    {
        Put("v1/admin/backup-target");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(SetBackupTargetCommand req, CancellationToken ct)
    {
        await handler.HandleAsync(req, ct);
        await SendNoContentAsync(ct);
    }
}

// The aggregate's own rules decide, so a 400 here and the domain never disagree.
public class SetBackupTargetValidator : Validator<SetBackupTargetCommand>
{
    public SetBackupTargetValidator()
    {
        RuleFor(x => x.Endpoint).Must(BackupTargetAggregate.IsValidEndpoint)
            .WithMessage("endpoint must be an https URL of at most 200 characters, without credentials, query or fragment");
        RuleFor(x => x.Region).Must(BackupTargetAggregate.IsValidRegion)
            .WithMessage("region must be 1 to 64 letters, digits or hyphens");
        RuleFor(x => x.Bucket).Must(BackupTargetAggregate.IsValidBucket)
            .WithMessage("bucket must be 1 to 63 characters of a-z, 0-9, '-' or '.'");
        RuleFor(x => x.AccessKeyId).Must(BackupTargetAggregate.IsValidAccessKeyId)
            .WithMessage("accessKeyId must be 1 to 128 characters without spaces");
        // Absent or empty keeps the stored secret; anything given must be a usable one.
        RuleFor(x => x.SecretAccessKey).Must(BackupTargetAggregate.IsValidSecretAccessKey)
            .When(x => !string.IsNullOrEmpty(x.SecretAccessKey))
            .WithMessage("secretAccessKey must be 1 to 256 characters without spaces");
    }
}
